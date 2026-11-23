using MeadowEmulator.Core;

namespace MeadowEmulator.Core.Tests;

public sealed class LogTailTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("emu-ui-tail").FullName;

    private string LogPath => Path.Combine(_dir, "uart.txt");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SkipsExistingContent()
    {
        File.WriteAllText(LogPath, "old\n");
        var tail = new LogTail(LogPath);
        File.AppendAllText(LogPath, "new\n");

        Assert.Equal(["new"], tail.Poll());
    }

    [Fact]
    public void HoldsPartialLineUntilComplete()
    {
        var tail = new LogTail(LogPath);
        File.WriteAllText(LogPath, "hel");
        Assert.Empty(tail.Poll());

        File.AppendAllText(LogPath, "lo\r\n");
        Assert.Equal(["hello"], tail.Poll());
    }

    [Fact]
    public void KeepsMultibyteCharacterSplitAcrossPolls()
    {
        var tail = new LogTail(LogPath);
        var bytes = "é\n"u8.ToArray();
        File.WriteAllBytes(LogPath, bytes[..1]);
        Assert.Empty(tail.Poll());

        using (var file = new FileStream(LogPath, FileMode.Append))
        {
            file.Write(bytes, 1, bytes.Length - 1);
        }

        Assert.Equal(["é"], tail.Poll());
    }

    [Fact]
    public void FollowsRotationToNewFile()
    {
        File.WriteAllText(LogPath, "previous run, a long line\n");
        var tail = new LogTail(LogPath);

        File.Move(LogPath, LogPath + ".1");
        File.WriteAllText(LogPath, "boot\n");

        Assert.Equal(["boot"], tail.Poll());
    }

    [Fact]
    public void DoesNotThrowWhileWriterHoldsFile()
    {
        var tail = new LogTail(LogPath);
        using var writer = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write("line\n"u8);
        writer.Flush();

        var exception = Record.Exception(() => tail.Poll());

        Assert.Null(exception);
    }

    [Fact]
    public void WaitsForMissingFile()
    {
        var tail = new LogTail(LogPath);
        Assert.Empty(tail.Poll());

        File.WriteAllText(LogPath, "first\n");
        Assert.Equal(["first"], tail.Poll());
    }
}

public sealed class SessionTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("emu-ui-session").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private string PidFile => Path.Combine(_repo, "build", "ui-renode.pid");

    private EmulatorProfile Profile => new(Path.Combine(_repo, "c.yaml"), OsVariant.Rebase, "F7CoreComputeV2", Coprocessor.Stub, 1, 0);

    private EmulatorSession CreateSession(string script, Func<int, bool>? isListening = null)
    {
        var path = Path.Combine(_repo, "run.sh");
        File.WriteAllText(path, "#!/bin/bash\n" + script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new EmulatorSession(_repo, new ToolEnvironment(_repo, "/usr/bin:/bin"), isListening ?? (_ => File.Exists(PidFile)))
        {
            QuitTimeout = TimeSpan.FromMilliseconds(300),
            PortClearTimeout = TimeSpan.FromMilliseconds(300),
            StartTimeout = TimeSpan.FromSeconds(10),
        };
    }

    private const string FakeRenode = """
        for a in "$@"; do case "$a" in --pid-file=*) p="${a#--pid-file=}";; esac; done
        echo "args: $*"
        echo $$ > "$p"
        exec sleep 30
        """;

    [Fact]
    public async Task StartsThenStopsByFallbackSignal()
    {
        var session = CreateSession(FakeRenode);
        var states = new List<SessionState>();
        var output = new List<string>();
        session.StateChanged += states.Add;
        session.OutputLine += line => { lock (output) output.Add(line); };

        await session.StartAsync(Profile, "/images/a.bin", useEspEmu: true);
        Assert.Equal(SessionState.Running, session.State);

        await session.StopAsync();

        Assert.Equal([SessionState.Starting, SessionState.Running, SessionState.Stopping, SessionState.Stopped], states);
        Assert.False(File.Exists(PidFile));
        lock (output)
        {
            var args = Assert.Single(output, l => l.StartsWith("args:", StringComparison.Ordinal));
            Assert.Contains($"--config={Profile.ConfigPath}", args);
            Assert.Contains("--mount=/images/a.bin", args);
            Assert.Contains("--esp-emu", args);
        }
    }

    [Fact]
    public async Task ReportsFailureWhenRunScriptExits()
    {
        var session = CreateSession("echo 'ERROR: nuttx.bin not found'\nexit 3\n", _ => false);
        var output = new List<string>();
        session.OutputLine += line => { lock (output) output.Add(line); };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Profile, null, false));

        Assert.Contains("code 3", error.Message);
        Assert.Equal(SessionState.Failed, session.State);
        lock (output)
        {
            Assert.Contains(output, l => l.Contains("nuttx.bin", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RefusesToStartWhenPortStaysBusy()
    {
        var session = CreateSession(FakeRenode, port => port == 4242);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Profile, null, false));

        Assert.Contains("4242", error.Message);
        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public async Task DetectsUnexpectedExit()
    {
        var session = CreateSession("""
            for a in "$@"; do case "$a" in --pid-file=*) p="${a#--pid-file=}";; esac; done
            echo $$ > "$p"
            sleep 0.5
            exit 9
            """);
        var failed = new TaskCompletionSource();
        session.StateChanged += s => { if (s == SessionState.Failed) failed.TrySetResult(); };

        await session.StartAsync(Profile, null, false);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionState.Failed, session.State);
    }
}
