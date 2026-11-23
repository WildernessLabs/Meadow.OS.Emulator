using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeadowEmulator.Core;

namespace MeadowEmulator.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var repoRoot = FindRepoRoot();
            desktop.MainWindow = new MainWindow(new MainViewModel(repoRoot, ToolEnvironment.FromCurrentProcess()));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>MEADOW_EMULATOR_REPO, or the folder above the app that holds run.sh.</summary>
    private static string FindRepoRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MEADOW_EMULATOR_REPO");
        if (!string.IsNullOrEmpty(configured) && File.Exists(Path.Combine(configured, "run.sh")))
        {
            return Path.GetFullPath(configured);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "run.sh")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Emulator repo not found. Set MEADOW_EMULATOR_REPO.");
    }
}
