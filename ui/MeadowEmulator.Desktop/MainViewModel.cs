using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeadowEmulator.Core;

namespace MeadowEmulator.Desktop;

/// <summary>An entry in the image list. A null image means an empty file system.</summary>
public sealed record ImageChoice(ImageInfo? Image)
{
    public string Label => Image is null
        ? "Empty (firmware formats it)"
        : $"{Image.Name}  (LittleFS v{Image.LfsMajor}.{Image.LfsMinor})";
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly string _repoRoot;
    private readonly ToolEnvironment _tools;
    private readonly EmulatorSession _session;
    private readonly DeployService _deploy;
    private readonly IReadOnlyList<EmulatorProfile> _allProfiles;

    private CancellationTokenSource? _tailCancellation;
    private CancellationTokenSource? _listenCancellation;
    private Task _listenTask = Task.CompletedTask;
    private CancellationTokenSource? _liveCancellation;
    private CancellationTokenSource? _buildCancellation;

    [ObservableProperty]
    private string _selectedOs = "Rebase";

    [ObservableProperty]
    private EmulatorProfile? _selectedProfile;

    [ObservableProperty]
    private ImageChoice? _selectedImage;

    [ObservableProperty]
    private bool _useEspEmu;

    [ObservableProperty]
    private bool _cleanBuild;

    [ObservableProperty]
    private string _projectPath = string.Empty;

    [ObservableProperty]
    private string _statusText = "Stopped";

    [ObservableProperty]
    private string _notice = string.Empty;

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private bool _isBuilding;

    public MainViewModel(string repoRoot, ToolEnvironment tools)
    {
        _repoRoot = repoRoot;
        _tools = tools;
        _session = new EmulatorSession(repoRoot, tools);
        _deploy = new DeployService(repoRoot, tools);
        _allProfiles = EmulatorProfile.LoadAll(repoRoot);
        _session.OutputLine += EmulatorLog.Add;
        _session.StateChanged += state => Dispatcher.UIThread.Post(() => OnSessionState(state));

        foreach (var tool in tools.Check().Where(t => !t.Found))
        {
            EmulatorLog.Add($"{(tool.Required ? "ERROR" : "Note")}: {tool.Name} was not found on the tool path.");
        }

        if (tools.Find("python3") is not null && !tools.PythonHasYaml())
        {
            EmulatorLog.Add("ERROR: python3 has no PyYAML, which run.sh needs. Run: python3 -m pip install pyyaml");
        }

        RefreshProfiles();
    }

    public IReadOnlyList<string> OsChoices { get; } = ["Rebase", "Legacy"];

    public ObservableCollection<EmulatorProfile> Profiles { get; } = [];

    public ObservableCollection<ImageChoice> Images { get; } = [];

    public LogPane AppLog { get; } = new("Application");

    public LogPane SysLog { get; } = new("Syslog");

    public LogPane EspLog { get; } = new("ESP");

    public LogPane EmulatorLog { get; } = new("Emulator and build");

    public string ImagesDirectory => Path.Combine(_repoRoot, "fs-images");

    private OsVariant Os => SelectedOs == "Legacy" ? OsVariant.Legacy : OsVariant.Rebase;

    private bool IsActive => _session.State is SessionState.Starting or SessionState.Running or SessionState.Stopping;

    private bool IsRunning => _session.State == SessionState.Running;

    private bool HasProject => !string.IsNullOrWhiteSpace(ProjectPath);

    /// <summary>Selects an image from anywhere, after validating it for the current OS.</summary>
    public void UseImageFile(string path)
    {
        var image = ImageCatalog.Inspect(path);
        if (!image.IsCompatibleWith(Os))
        {
            Notice = image.Error ?? $"{image.Name} is LittleFS v{image.LfsMajor}; {SelectedOs} needs v{(Os == OsVariant.Legacy ? 1 : 2)}.";
            return;
        }

        var choice = Images.FirstOrDefault(c => c.Image?.Path == image.Path);
        if (choice is null)
        {
            choice = new ImageChoice(image);
            Images.Add(choice);
        }

        SelectedImage = choice;
        UpdateNotice();
    }

    /// <summary>Stops everything when the window closes.</summary>
    public async Task ShutdownAsync()
    {
        _buildCancellation?.Cancel();
        _liveCancellation?.Cancel();
        await StopListeningAsync();
        await _session.StopAsync();
        _tailCancellation?.Cancel();
    }

    /// <summary>Bakes the selected project into the given image and selects it.</summary>
    public async Task BakeImageAsync(string outputPath)
    {
        if (SelectedProfile is not { } profile || !CanBuild())
        {
            return;
        }

        await RunBuildAsync($"Baking {Path.GetFileName(outputPath)}", async token =>
        {
            await _deploy.BakeAsync(ProjectPath, profile, outputPath, EmulatorLog.Add, token);
            RefreshImages();
            UseImageFile(outputPath);
            EmulatorLog.Add(IsRunning
                ? "Image built. Restart the emulator to boot from it."
                : "Image built and selected.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        SysLog.Clear();
        AppLog.Clear();
        EspLog.Clear();
        StartTails(profile);
        try
        {
            await _session.StartAsync(profile, SelectedImage?.Image?.Path, UseEspEmu);
            StartListening();
        }
        catch (Exception e)
        {
            _tailCancellation?.Cancel();
            StatusText = $"Failed: {e.Message}";
        }
    }

    private bool CanStart() => !IsActive && !IsBuilding && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        _liveCancellation?.Cancel();
        await StopListeningAsync();
        await _session.StopAsync();
    }

    private bool CanStop() => _session.State is SessionState.Running or SessionState.Starting;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        try
        {
            await _session.SendMonitorCommandAsync("machine Reset");
            EmulatorLog.Add("Reset sent.");
        }
        catch (Exception e)
        {
            EmulatorLog.Add($"ERROR: reset failed: {e.Message}");
        }
    }

    private bool CanReset() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task DeployLiveAsync()
    {
        await StopListeningAsync();
        _liveCancellation = new CancellationTokenSource();
        IsLive = true;
        RefreshCommands();
        AppLog.Add($"--- Deploying {Path.GetFileName(ProjectPath)} ---");
        try
        {
            var exit = await _deploy.RunLiveAsync(ProjectPath, AppLog.Add, _liveCancellation.Token);
            AppLog.Add($"--- meadow app run exited with code {exit} ---");
        }
        catch (OperationCanceledException)
        {
            AppLog.Add("--- Detached from the app ---");
        }
        catch (Exception e)
        {
            AppLog.Add($"ERROR: {e.Message}");
        }
        finally
        {
            _liveCancellation.Dispose();
            _liveCancellation = null;
            IsLive = false;
            RefreshCommands();
            if (IsRunning)
            {
                StartListening();
            }
        }
    }

    private bool CanDeploy() => IsRunning && HasProject && !IsLive && !IsBuilding;

    [RelayCommand(CanExecute = nameof(IsLive))]
    private void Detach() => _liveCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildFirmwareAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        await RunBuildAsync($"Building firmware for {profile.Name}", token =>
            _deploy.BuildFirmwareAsync(profile, CleanBuild, EmulatorLog.Add, token));
    }

    // The firmware build replaces files the running emulator uses.
    private bool CanBuild() => !IsActive && !IsBuilding && !IsLive && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(IsBuilding))]
    private void CancelBuild() => _buildCancellation?.Cancel();

    private async Task RunBuildAsync(string description, Func<CancellationToken, Task> work)
    {
        _buildCancellation = new CancellationTokenSource();
        IsBuilding = true;
        StatusText = description;
        RefreshCommands();
        EmulatorLog.Add($"--- {description} ---");
        try
        {
            await work(_buildCancellation.Token);
            StatusText = "Build finished";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Build cancelled";
        }
        catch (Exception e)
        {
            EmulatorLog.Add($"ERROR: {e.Message}");
            StatusText = "Build failed. See the Emulator tab.";
        }
        finally
        {
            _buildCancellation.Dispose();
            _buildCancellation = null;
            IsBuilding = false;
            RefreshCommands();
        }
    }

    private void StartTails(EmulatorProfile profile)
    {
        _tailCancellation?.Cancel();
        _tailCancellation = new CancellationTokenSource();
        var token = _tailCancellation.Token;
        var interval = TimeSpan.FromMilliseconds(200);

        // Constructed before launch, so only this run's output is shown.
        _ = new LogTail(profile.SyslogPath).FollowAsync(SysLog.Add, interval, token);
        if (UseEspEmu)
        {
            var espLog = Path.Combine(profile.BuildDirectory(_repoRoot), "esp-emu.log");
            _ = new LogTail(espLog).FollowAsync(EspLog.Add, interval, token);
            _ = new LogTail("/tmp/renode-esp-uart.txt").FollowAsync(EspLog.Add, interval, token);
        }
    }

    /// <summary>Keeps one `meadow listen` on HCOM. It owns the port unless a live deploy does.</summary>
    private void StartListening()
    {
        _listenCancellation = new CancellationTokenSource();
        var token = _listenCancellation.Token;
        _listenTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _deploy.ListenAsync(AppLog.Add, token);
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    AppLog.Add($"ERROR: {e.Message}");
                    return;
                }
            }
        }, CancellationToken.None);
    }

    private async Task StopListeningAsync()
    {
        if (_listenCancellation is null)
        {
            return;
        }

        _listenCancellation.Cancel();
        await _listenTask;
        _listenCancellation.Dispose();
        _listenCancellation = null;
    }

    private void OnSessionState(SessionState state)
    {
        if (!(state == SessionState.Failed && StatusText.StartsWith("Failed", StringComparison.Ordinal)))
        {
            StatusText = state.ToString();
        }

        if (state is SessionState.Stopped or SessionState.Failed)
        {
            _liveCancellation?.Cancel();
            _tailCancellation?.Cancel();
            _ = StopListeningAsync();
        }

        RefreshCommands();
    }

    partial void OnSelectedOsChanged(string value) => RefreshProfiles();

    partial void OnSelectedProfileChanged(EmulatorProfile? value)
    {
        UpdateNotice();
        RefreshCommands();
    }

    partial void OnSelectedImageChanged(ImageChoice? value) => UpdateNotice();

    partial void OnProjectPathChanged(string value)
    {
        UpdateNotice();
        RefreshCommands();
    }

    partial void OnIsBuildingChanged(bool value) => RefreshCommands();

    partial void OnIsLiveChanged(bool value) => RefreshCommands();

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _allProfiles.Where(p => p.Os == Os))
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault();
        UseEspEmu = SelectedProfile?.Coprocessor == Coprocessor.EspEmu;
        RefreshImages();
    }

    private void RefreshImages()
    {
        var previous = SelectedImage?.Image?.Path;
        Images.Clear();
        Images.Add(new ImageChoice(null));
        foreach (var image in ImageCatalog.List(ImagesDirectory).Where(i => i.IsCompatibleWith(Os)))
        {
            Images.Add(new ImageChoice(image));
        }

        SelectedImage = Images.FirstOrDefault(c => c.Image?.Path == previous) ?? Images[0];
    }

    private void UpdateNotice()
    {
        var notes = new List<string> { "File system changes are lost when the emulator stops." };
        if (Os == OsVariant.Legacy)
        {
            notes.Add("Legacy syslog is partial: ESP log lines do not reach it.");
        }

        if (HasProject && SelectedProfile is { } profile && File.Exists(ProjectPath))
        {
            var board = DeployService.FindBoard(ProjectPath);
            if (board is not null && board != profile.Board)
            {
                notes.Insert(0, $"Board mismatch: the app targets {board}, this config emulates {profile.Board}.");
            }
        }

        Notice = string.Join(Environment.NewLine, notes);
    }

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        DeployLiveCommand.NotifyCanExecuteChanged();
        DetachCommand.NotifyCanExecuteChanged();
        BuildFirmwareCommand.NotifyCanExecuteChanged();
        CancelBuildCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanBake));
        OnPropertyChanged(nameof(CanChangeSetup));
    }

    /// <summary>Baking needs a project and no other build, and can run beside the emulator.</summary>
    public bool CanBake => HasProject && !IsBuilding && !IsLive && SelectedProfile is not null;

    public bool CanChangeSetup => !IsActive && !IsBuilding;
}
