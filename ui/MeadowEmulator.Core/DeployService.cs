using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MeadowEmulator.Core;

/// <summary>Builds firmware and apps, and deploys apps, through the existing tools.</summary>
public sealed partial class DeployService(string repoRoot, ToolEnvironment tools)
{
    private const string HcomRoute = "socket://localhost:4242";
    private const string Configuration = "Release";

    /// <summary>
    /// Builds, deploys and streams output until cancelled. Passes -s so the
    /// user's saved Meadow.CLI route is left alone.
    /// </summary>
    public Task<int> RunLiveAsync(string projectFile, Action<string> onLine, CancellationToken cancellation) =>
        RunAsync(
            Meadow(),
            ["app", "run", ProjectDirectory(projectFile), "-c", Configuration, "-s", HcomRoute],
            line => { if (!IsSpinnerFrame(line)) onLine(line); },
            cancellation);

    /// <summary>Meadow.CLI's progress spinner arrives as lines when output is redirected.</summary>
    public static bool IsSpinnerFrame(string line) =>
        line.Trim() is "|" or "/" or "-" or "\\";

    public Task<int> ListenAsync(Action<string> onLine, CancellationToken cancellation) =>
        RunAsync(
            Meadow(),
            ["listen", "-s", HcomRoute],
            line => { if (!IsConnectRetry(line)) onLine(line); },
            cancellation);

    /// <summary>HCOM refuses connections until the firmware's port is up; listening retries.</summary>
    public static bool IsConnectRetry(string line) =>
        line.StartsWith("No device found:", StringComparison.Ordinal);

    /// <summary>
    /// Packs the app's publish folder into a LittleFS image. Only `meadow app run`
    /// creates that folder, because it trims against the connected OS's libraries
    /// (`meadow app build` does not publish), so a live deploy must come first.
    /// </summary>
    public async Task BakeAsync(string projectFile, EmulatorProfile profile, string outputImage, Action<string> onLine, CancellationToken cancellation)
    {
        if (!ImageCatalog.IsSafeForRenode(outputImage))
        {
            throw new InvalidOperationException("The image path cannot contain spaces or quotes.");
        }

        var directory = ProjectDirectory(projectFile);
        var publish = FindPublishDirectory(directory)
            ?? throw new InvalidOperationException("The app has no publish folder. Deploy it live once, then bake.");
        if (IsPublishStale(directory, publish))
        {
            throw new InvalidOperationException("The app's sources are newer than its publish folder. Deploy it live again, then bake.");
        }

        var imageExit = await RunAsync(
            Path.Combine(repoRoot, "tools", "make-lfs-image.sh"),
            [$"--config={profile.ConfigPath}", publish, outputImage],
            onLine,
            cancellation);
        if (imageExit != 0)
        {
            throw new InvalidOperationException($"make-lfs-image.sh failed with code {imageExit}.");
        }
    }

    public async Task BuildFirmwareAsync(EmulatorProfile profile, bool clean, Action<string> onLine, CancellationToken cancellation)
    {
        List<string> arguments = [$"--config={profile.ConfigPath}"];
        if (clean)
        {
            arguments.Add("--clean");
        }

        var exit = await RunAsync(Path.Combine(repoRoot, "build-meadow.os-emulated.sh"), arguments, onLine, cancellation);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Firmware build failed with code {exit}.");
        }
    }

    /// <summary>Finds the board in the app's App&lt;Board&gt; declaration, if any.</summary>
    public static string? FindBoard(string projectFile)
    {
        var directory = ProjectDirectory(projectFile);
        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(directory, f));
        foreach (var source in sources)
        {
            var match = AppBoardPattern().Match(File.ReadAllText(source));
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>The newest bin/Release/net*/publish folder of a project.</summary>
    public static string? FindPublishDirectory(string projectDirectory)
    {
        var bin = Path.Combine(projectDirectory, "bin", Configuration);
        return Directory.Exists(bin)
            ? Directory.EnumerateDirectories(bin, "net*")
                .Select(d => Path.Combine(d, "publish"))
                .Where(Directory.Exists)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    /// <summary>True when a source or project file is newer than everything published.</summary>
    public static bool IsPublishStale(string projectDirectory, string publishDirectory)
    {
        var published = Directory.EnumerateFiles(publishDirectory, "*", SearchOption.AllDirectories)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        return Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(projectDirectory, f))
            .Where(f => Path.GetExtension(f) is ".cs" or ".csproj" or ".yaml" or ".yml")
            .Any(f => File.GetLastWriteTimeUtc(f) > published);
    }

    /// <summary>
    /// Finds a sibling Meadow.Core checkout for apps that build against source,
    /// such as Rebase's MiniTests. A GUI launch does not inherit MEADOW_CORE_DIR.
    /// </summary>
    public static string? FindMeadowCore(string repoRoot)
    {
        string[] candidates =
        [
            Path.Combine(repoRoot, "..", "Meadow.Core"),
            Path.Combine(repoRoot, "..", "..", "Meadow.Core"),
            Path.Combine(repoRoot, "..", "meadow-3.0", "Meadow.Core"),
            Path.Combine(repoRoot, "..", "..", "meadow-3.0", "Meadow.Core"),
        ];

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(c => File.Exists(Path.Combine(c, "Source", "implementations", "f7", "Meadow.F7", "Meadow.F7.csproj")));
    }

    public static string ProjectDirectory(string projectFile) =>
        Directory.Exists(projectFile) ? projectFile : Path.GetDirectoryName(Path.GetFullPath(projectFile))!;

    private static bool IsBuildOutput(string root, string file)
    {
        var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private string Meadow() =>
        tools.Find("meadow") ?? throw new InvalidOperationException("Meadow.CLI (meadow) was not found. Run setup.sh.");

    private async Task<int> RunAsync(string file, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.Environment["PATH"] = tools.PathVariable;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEADOW_CORE_DIR"))
            && FindMeadowCore(repoRoot) is { } meadowCore)
        {
            start.Environment["MEADOW_CORE_DIR"] = meadowCore;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return process.ExitCode;
    }

    [GeneratedRegex(@"\bApp\s*<\s*(F7\w+)\s*>")]
    private static partial Regex AppBoardPattern();
}
