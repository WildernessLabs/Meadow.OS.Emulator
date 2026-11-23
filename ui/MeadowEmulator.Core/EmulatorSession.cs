using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace MeadowEmulator.Core;

public enum SessionState { Stopped, Starting, Running, Stopping, Failed }

/// <summary>
/// Runs one emulator through run.sh. Readiness is read from the listener table
/// and never by connecting, because HCOM and esp-emu treat a connection as a client.
/// </summary>
public sealed class EmulatorSession
{
    /// <summary>Monitor, HCOM, ESP control, esp-emu co-simulation and esp-emu UART.</summary>
    private static readonly int[] FixedPorts = [4242, 4243, 5590, 5591];

    private readonly string _repoRoot;
    private readonly ToolEnvironment _tools;
    private readonly Func<int, bool> _isListening;
    private readonly string _pidFile;
    private Process? _process;
    private EmulatorProfile? _profile;

    public EmulatorSession(string repoRoot, ToolEnvironment tools, Func<int, bool>? isListening = null)
    {
        _repoRoot = repoRoot;
        _tools = tools;
        _isListening = isListening ?? IsPortListening;
        _pidFile = Path.Combine(repoRoot, "build", "ui-renode.pid");
    }

    public SessionState State { get; private set; } = SessionState.Stopped;

    public TimeSpan PortClearTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan QuitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public event Action<SessionState>? StateChanged;

    /// <summary>Output of run.sh and Renode.</summary>
    public event Action<string>? OutputLine;

    public async Task StartAsync(EmulatorProfile profile, string? imagePath, bool useEspEmu, CancellationToken cancellation = default)
    {
        if (State is SessionState.Starting or SessionState.Running or SessionState.Stopping)
        {
            throw new InvalidOperationException("The emulator is already running.");
        }

        SetState(SessionState.Starting);
        try
        {
            await WaitForPortsToClearAsync(profile, cancellation);

            Directory.CreateDirectory(Path.GetDirectoryName(_pidFile)!);
            File.Delete(_pidFile); // Renode does not remove it on exit.

            _profile = profile;
            _process = Launch(profile, imagePath, useEspEmu);
            await WaitUntilReadyAsync(profile, _process, cancellation);
            SetState(SessionState.Running);
            _ = WatchForExitAsync(_process);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync();
            SetState(SessionState.Stopped);
            throw;
        }
        catch (Exception e)
        {
            OutputLine?.Invoke($"ERROR: {e.Message}");
            await StopProcessAsync();
            SetState(SessionState.Failed);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (State is not (SessionState.Running or SessionState.Starting))
        {
            return;
        }

        SetState(SessionState.Stopping);
        await StopProcessAsync();
        SetState(SessionState.Stopped);
    }

    /// <summary>Sends one command to the Renode monitor.</summary>
    public async Task SendMonitorCommandAsync(string command)
    {
        var port = _profile?.MonitorPort ?? throw new InvalidOperationException("The emulator is not running.");
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), timeout.Token);
        await stream.FlushAsync(timeout.Token);
        await Task.Delay(200, CancellationToken.None); // Let Renode read it before the socket closes.
    }

    private Process Launch(EmulatorProfile profile, string? imagePath, bool useEspEmu)
    {
        var start = new ProcessStartInfo(Path.Combine(_repoRoot, "run.sh"))
        {
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.Environment["PATH"] = _tools.PathVariable;

        // Always the config: --legacy/--rebase alone leave the hooks on the config's OS.
        start.ArgumentList.Add($"--config={profile.ConfigPath}");
        start.ArgumentList.Add($"--pid-file={_pidFile}");
        if (imagePath is not null)
        {
            start.ArgumentList.Add($"--mount={imagePath}");
        }

        if (useEspEmu)
        {
            start.ArgumentList.Add("--esp-emu");
        }

        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputLine?.Invoke(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task WaitForPortsToClearAsync(EmulatorProfile profile, CancellationToken cancellation)
    {
        var ports = FixedPorts.Append(profile.MonitorPort).ToArray();
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var busy = await Task.Run(() => ports.Where(_isListening).ToArray(), cancellation);
            if (busy.Length == 0)
            {
                return;
            }

            if (deadline.Elapsed >= PortClearTimeout)
            {
                throw new InvalidOperationException(
                    $"Port(s) {string.Join(", ", busy)} are in use. Another emulator may be running.");
            }

            await Task.Delay(250, cancellation);
        }
    }

    private async Task WaitUntilReadyAsync(EmulatorProfile profile, Process process, CancellationToken cancellation)
    {
        var deadline = Stopwatch.StartNew();
        while (ReadRenodePid() is null || !await Task.Run(() => _isListening(profile.MonitorPort), cancellation))
        {
            if (process.HasExited)
            {
                await process.WaitForExitAsync(cancellation); // Flushes the remaining output.
                throw new InvalidOperationException($"run.sh exited with code {process.ExitCode} before the emulator started.");
            }

            if (deadline.Elapsed >= StartTimeout)
            {
                throw new TimeoutException($"The emulator did not start within {StartTimeout.TotalSeconds:0} s.");
            }

            await Task.Delay(200, cancellation);
        }
    }

    private async Task WatchForExitAsync(Process process)
    {
        await process.WaitForExitAsync();
        if (ReferenceEquals(process, _process) && State == SessionState.Running)
        {
            _process = null;
            OutputLine?.Invoke($"Emulator exited with code {process.ExitCode}.");
            SetState(process.ExitCode == 0 ? SessionState.Stopped : SessionState.Failed);
        }
    }

    /// <summary>
    /// Stops Renode and lets run.sh exit, so its EXIT trap stops esp-emu. Killing
    /// the process tree is the last resort.
    /// </summary>
    private async Task StopProcessAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await SendMonitorCommandAsync("quit");
                }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
                {
                    OutputLine?.Invoke("Monitor not reachable; stopping Renode by signal.");
                }

                if (!await ExitsWithinAsync(process, QuitTimeout))
                {
                    SignalRenode();
                }

                if (!await ExitsWithinAsync(process, QuitTimeout))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            _process = null;
            process.Dispose();
            File.Delete(_pidFile);
        }
    }

    private void SignalRenode()
    {
        if (ReadRenodePid() is not { } pid)
        {
            return;
        }

        try
        {
            using var renode = Process.GetProcessById(pid);
            renode.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    private int? ReadRenodePid()
    {
        try
        {
            return int.TryParse(File.ReadAllText(_pidFile).Trim(), out var pid) ? pid : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return null;
        }
    }

    private static async Task<bool> ExitsWithinAsync(Process process, TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the system's listener table without connecting. Uses lsof or ss
    /// because IPGlobalProperties.GetActiveTcpListeners returned nothing on
    /// macOS while Renode was listening.
    /// </summary>
    private bool IsPortListening(int port)
    {
        ProcessStartInfo start;
        if (_tools.Find("lsof") is { } lsof)
        {
            start = new ProcessStartInfo(lsof);
            foreach (var argument in new[] { "-nP", $"-iTCP:{port}", "-sTCP:LISTEN", "-t" })
            {
                start.ArgumentList.Add(argument);
            }
        }
        else if (_tools.Find("ss") is { } ss)
        {
            start = new ProcessStartInfo(ss);
            foreach (var argument in new[] { "-H", "-ltn", $"sport = :{port}" })
            {
                start.ArgumentList.Add(argument);
            }
        }
        else
        {
            throw new InvalidOperationException("Neither lsof nor ss was found, so ports cannot be checked.");
        }

        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not run the port check.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(3000))
        {
            process.Kill();
            throw new TimeoutException("The port check timed out.");
        }

        return output.GetAwaiter().GetResult().Trim().Length > 0;
    }

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
