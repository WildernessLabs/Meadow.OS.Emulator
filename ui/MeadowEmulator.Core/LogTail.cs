using System.Text;

namespace MeadowEmulator.Core;

/// <summary>
/// Follows a log file by path. Renode renames the previous log at startup and
/// creates a new one, so a change of creation time or a shrink restarts at the
/// beginning of the file. Content present at construction is skipped.
/// </summary>
public sealed class LogTail
{
    private readonly string _path;
    private readonly StringBuilder _partial = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _offset;
    private DateTime _created;

    public LogTail(string path)
    {
        _path = path;
        var info = new FileInfo(path);
        if (info.Exists)
        {
            _offset = info.Length;
            _created = info.CreationTimeUtc;
        }
    }

    /// <summary>Returns the complete lines added since the last call.</summary>
    public IReadOnlyList<string> Poll()
    {
        var lines = new List<string>();
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            return lines;
        }

        if (info.CreationTimeUtc != _created || info.Length < _offset)
        {
            _created = info.CreationTimeUtc;
            _offset = 0;
            _partial.Clear();
            _decoder = Encoding.UTF8.GetDecoder();
        }

        if (info.Length == _offset)
        {
            return lines;
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_offset, SeekOrigin.Begin);
            var bytes = new byte[64 * 1024];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            int read;
            while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                _offset += read;
                var count = _decoder.GetChars(bytes, 0, read, chars, 0);
                Append(chars.AsSpan(0, count), lines);
            }
        }
        catch (IOException)
        {
            // Rotated or locked at this moment; the next poll picks it up. Reading
            // Renode's open log also needs System.IO.DisableFileLocking in the host
            // (see ui/Directory.Build.props), because .NET's advisory locks conflict.
        }

        return lines;
    }

    /// <summary>Polls until cancelled.</summary>
    public async Task FollowAsync(Action<string> onLine, TimeSpan interval, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                foreach (var line in Poll())
                {
                    onLine(line);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException)
        {
            // Normal stop: deliver what the process wrote just before it ended.
            foreach (var line in Poll())
            {
                onLine(line);
            }
        }
    }

    private void Append(ReadOnlySpan<char> text, List<string> lines)
    {
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines.Add(_partial.ToString().TrimEnd('\r'));
                _partial.Clear();
            }
            else
            {
                _partial.Append(c);
            }
        }
    }
}
