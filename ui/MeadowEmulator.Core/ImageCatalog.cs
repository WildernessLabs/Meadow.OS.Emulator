using System.Buffers.Binary;
using System.Text;

namespace MeadowEmulator.Core;

/// <summary>A file system image and the result of validating it.</summary>
public sealed record ImageInfo(string Path, long Size, int LfsMajor, int LfsMinor, string? Error)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public bool IsValid => Error is null;

    public bool IsCompatibleWith(OsVariant os) => IsValid && LfsMajor == (os == OsVariant.Legacy ? 1 : 2);
}

/// <summary>
/// Finds and validates LittleFS images. The checks mirror the --mount validation
/// in run.sh, which remains the authority at launch.
/// </summary>
public static class ImageCatalog
{
    /// <summary>Size of the QSPI file system partition at offset 0x500000.</summary>
    public const long PartitionSize = 61_865_984;

    private const int SuperblockPairSize = 2 * 4096;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("littlefs");

    public static IReadOnlyList<ImageInfo> List(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.bin")
                .Select(Inspect)
                .OrderBy(i => i.Name, StringComparer.Ordinal)
                .ToList()
            : [];

    public static ImageInfo Inspect(string path)
    {
        long size;
        var head = new byte[SuperblockPairSize];
        int read;
        try
        {
            size = new FileInfo(path).Length;
            using var file = File.OpenRead(path);
            read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ImageInfo(path, 0, 0, 0, $"Cannot read image: {e.Message}");
        }

        if (size > PartitionSize)
        {
            return new ImageInfo(path, size, 0, 0, $"Image is {size} bytes, larger than the {PartitionSize} byte partition.");
        }

        if (!IsSafeForRenode(path))
        {
            return new ImageInfo(path, size, 0, 0, "Path contains spaces or quotes, which the Renode script cannot load.");
        }

        var data = head.AsSpan(0, read);
        var pos = data.IndexOf(Magic);
        if (pos < 0)
        {
            return new ImageInfo(path, size, 0, 0, "No LittleFS superblock in blocks 0-1.");
        }

        // LittleFS v1 stores the version just before the magic, v2 after it (past a tag).
        var v1 = pos >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data[(pos - 4)..]) : 0;
        var v2 = pos + 16 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 12)..]) : 0;
        uint version;
        if (v1 >> 16 == 1)
        {
            version = v1;
        }
        else if (v2 >> 16 == 2)
        {
            version = v2;
        }
        else
        {
            return new ImageInfo(path, size, 0, 0, "Cannot read the LittleFS version.");
        }

        return new ImageInfo(path, size, (int)(version >> 16), (int)(version & 0xFFFF), null);
    }

    /// <summary>run.sh writes the path unquoted into a generated .resc file.</summary>
    public static bool IsSafeForRenode(string path) =>
        !path.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'');
}
