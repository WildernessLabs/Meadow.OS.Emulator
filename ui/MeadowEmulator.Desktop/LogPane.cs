using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MeadowEmulator.Desktop;

/// <summary>A bounded log view. Lines may arrive on any thread and are shown in batches.</summary>
public sealed partial class LogPane : ObservableObject
{
    private const int MaxLines = 20_000;

    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly List<string> _all = [];
    private int _flushQueued;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private bool _isPaused;

    public LogPane(string title) => Title = title;

    public string Title { get; }

    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>Raised after new lines are shown, so the view can scroll.</summary>
    public event Action? LinesAdded;

    public void Add(string line)
    {
        _incoming.Enqueue(line);

        // One queued flush at a time, below input priority, so a busy log batches
        // its lines and the window stays responsive.
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
    }

    public string AllText()
    {
        Flush();
        return string.Join(Environment.NewLine, _all);
    }

    [RelayCommand]
    public void Clear()
    {
        _incoming.Clear();
        _all.Clear();
        Lines.Clear();
    }

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            Rebuild();
        }
    }

    private void Flush()
    {
        Volatile.Write(ref _flushQueued, 0);
        if (_incoming.IsEmpty)
        {
            return;
        }

        var batch = new List<string>();
        while (_incoming.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        _all.AddRange(batch);
        var trimmed = _all.Count > MaxLines;
        if (trimmed)
        {
            _all.RemoveRange(0, _all.Count - MaxLines);
        }

        if (IsPaused)
        {
            return;
        }

        if (trimmed)
        {
            Rebuild();
        }
        else
        {
            foreach (var line in batch.Where(Matches))
            {
                Lines.Add(line);
            }

            LinesAdded?.Invoke();
        }
    }

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var line in _all.Where(Matches))
        {
            Lines.Add(line);
        }

        LinesAdded?.Invoke();
    }

    private bool Matches(string line) =>
        string.IsNullOrEmpty(Filter) || line.Contains(Filter, StringComparison.OrdinalIgnoreCase);
}
