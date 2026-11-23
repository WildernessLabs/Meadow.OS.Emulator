using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MeadowEmulator.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _shutdownComplete;

    public MainWindow() : this(null!)
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Holds the close until Renode and its helpers have stopped.</summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        try
        {
            await _viewModel.ShutdownAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private async void OnBrowseImage(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a LittleFS image",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_viewModel.ImagesDirectory),
            FileTypeFilter = [new FilePickerFileType("LittleFS image") { Patterns = ["*.bin"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _viewModel.UseImageFile(path);
        }
    }

    private async void OnBrowseProject(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a Meadow app project",
            FileTypeFilter = [new FilePickerFileType("C# project") { Patterns = ["*.csproj"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _viewModel.ProjectPath = path;
        }
    }

    private async void OnBakeImage(object? sender, RoutedEventArgs e)
    {
        var appName = Path.GetFileNameWithoutExtension(_viewModel.ProjectPath).ToLowerInvariant().Replace(' ', '-');
        var suffix = _viewModel.SelectedOs.ToLowerInvariant();
        Directory.CreateDirectory(_viewModel.ImagesDirectory);

        // The save dialog asks before replacing an existing image.
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the baked image",
            SuggestedFileName = $"{appName}-{suffix}.bin",
            DefaultExtension = "bin",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_viewModel.ImagesDirectory),
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await _viewModel.BakeImageAsync(path);
        }
    }
}
