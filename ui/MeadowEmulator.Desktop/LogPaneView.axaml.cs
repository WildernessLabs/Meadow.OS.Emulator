using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MeadowEmulator.Desktop;

public sealed partial class LogPaneView : UserControl
{
    private LogPane? _pane;

    public LogPaneView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_pane is not null)
        {
            _pane.LinesAdded -= ScrollToEnd;
        }

        _pane = DataContext as LogPane;
        if (_pane is not null)
        {
            _pane.LinesAdded += ScrollToEnd;
        }
    }

    private void ScrollToEnd()
    {
        if (_pane is { IsPaused: false, Lines.Count: > 0 } pane)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (pane.Lines.Count > 0)
                {
                    LinesList.ScrollIntoView(pane.Lines.Count - 1);
                }
            }, DispatcherPriority.Background);
        }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var selected = LinesList.SelectedItems?.Cast<string>().ToList();
        if (selected is { Count: > 0 } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, selected));
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_pane is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save {_pane.Title} log",
            SuggestedFileName = $"{_pane.Title.Split(' ')[0].ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(_pane.AllText());
    }
}
