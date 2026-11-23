using System.Diagnostics;

namespace MeadowEmulator.Core;

public sealed record ToolStatus(string Name, string? Path, bool Required)
{
    public bool Found => Path is not null;
}

/// <summary>
/// Builds the tool search path. An app launched from Finder or a desktop menu
/// gets a minimal PATH without the user's dotnet tools or Homebrew.
/// </summary>
public sealed class ToolEnvironment
{
    private static readonly (string Name, bool Required)[] KnownTools =
    [
        ("dotnet", true),
        ("python3", true),
        ("meadow", true),
        ("arm-none-eabi-gcc", false),
        ("esp-emu", false),
    ];

    /// <summary>
    /// The user's own paths come first, so tools resolve as they do in a terminal
    /// (for example a pyenv python3 that has PyYAML, ahead of Homebrew's).
    /// </summary>
    public ToolEnvironment(string home, string? inheritedPath, string? loginShellPath = null)
    {
        string[] fallbacks =
        [
            Path.Combine(home, ".dotnet", "tools"),
            Path.Combine(home, ".local", "bin"),
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin",
        ];

        SearchPath = Split(loginShellPath).Concat(Split(inheritedPath)).Concat(fallbacks).Distinct().ToList();
    }

    public IReadOnlyList<string> SearchPath { get; }

    /// <summary>Value for the PATH variable of child processes.</summary>
    public string PathVariable => string.Join(':', SearchPath);

    public static ToolEnvironment FromCurrentProcess() =>
        new(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("PATH"),
            ReadLoginShellPath());

    /// <summary>run.sh's config parser needs PyYAML in the python3 it finds.</summary>
    public bool PythonHasYaml()
    {
        if (Find("python3") is not { } python)
        {
            return false;
        }

        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.Environment["PATH"] = PathVariable;
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import yaml");
        return RunQuietly(start, out _) == 0;
    }

    /// <summary>Asks the user's shell for PATH, since a Finder launch does not inherit it.</summary>
    private static string? ReadLoginShellPath()
    {
        const string Marker = "__MEADOW_EMU_PATH__";
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
        {
            shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
        }

        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-ilc");
        start.ArgumentList.Add($"printf '{Marker}%s{Marker}' \"$PATH\"");
        if (RunQuietly(start, out var output) != 0)
        {
            return null;
        }

        var first = output.IndexOf(Marker, StringComparison.Ordinal);
        var last = output.LastIndexOf(Marker, StringComparison.Ordinal);
        return first >= 0 && last > first ? output[(first + Marker.Length)..last] : null;
    }

    private static int? RunQuietly(ProcessStartInfo start, out string output)
    {
        output = string.Empty;
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            output = read.GetAwaiter().GetResult();
            return process.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Split(string? path) =>
        (path ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries);

    public string? Find(string tool) =>
        SearchPath.Select(dir => Path.Combine(dir, tool)).FirstOrDefault(IsExecutable);

    public IReadOnlyList<ToolStatus> Check() =>
        KnownTools.Select(t => new ToolStatus(t.Name, Find(t.Name), t.Required)).ToList();

    private static bool IsExecutable(string path) =>
        File.Exists(path)
        && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
