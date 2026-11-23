using System.Buffers.Binary;
using System.Text;
using MeadowEmulator.Core;

namespace MeadowEmulator.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("emu-ui-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string yaml)
    {
        var path = Path.Combine(_dir, "test.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private string WriteImage(byte[] content, string name = "image.bin")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] ImageV1(uint version = 0x0001_0001)
    {
        var data = new byte[8192];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), version);
        Encoding.ASCII.GetBytes("littlefs").CopyTo(data, 40);
        return data;
    }

    private static byte[] ImageV2(uint version = 0x0002_0009)
    {
        var data = new byte[8192];
        Encoding.ASCII.GetBytes("littlefs").CopyTo(data, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), version);
        return data;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "run.sh")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void Profile_AppliesReadConfigDefaults()
    {
        var profile = EmulatorProfile.Load(WriteConfig("board: F7FeatherV2\n"));

        Assert.Equal(OsVariant.Legacy, profile.Os);
        Assert.Equal(Coprocessor.Stub, profile.Coprocessor);
        Assert.Equal(1234, profile.MonitorPort);
        Assert.Equal(0, profile.GdbPort);
    }

    [Theory]
    [InlineData("os: nope\n")]
    [InlineData("board: Nope\n")]
    [InlineData("coprocessor: nope\n")]
    [InlineData("ports:\n  monitor: 70000\n")]
    public void Profile_RejectsInvalidValues(string yaml) =>
        Assert.Throws<InvalidDataException>(() => EmulatorProfile.Load(WriteConfig(yaml)));

    [Fact]
    public void Profile_LoadsRepoConfigsWithRebaseFirst()
    {
        var profiles = EmulatorProfile.LoadAll(RepoRoot());

        Assert.Equal(OsVariant.Rebase, profiles[0].Os);
        Assert.Contains(profiles, p => p is { Name: "legacy-ccm", Os: OsVariant.Legacy, Board: "F7CoreComputeV2" });
        Assert.Equal("/tmp/renode-uart-meadow.txt", profiles[0].SyslogPath);
    }

    [Fact]
    public void Image_RecognizesV1()
    {
        var info = ImageCatalog.Inspect(WriteImage(ImageV1()));

        Assert.True(info.IsValid, info.Error);
        Assert.Equal(1, info.LfsMajor);
        Assert.True(info.IsCompatibleWith(OsVariant.Legacy));
        Assert.False(info.IsCompatibleWith(OsVariant.Rebase));
    }

    [Fact]
    public void Image_RecognizesV2()
    {
        var info = ImageCatalog.Inspect(WriteImage(ImageV2()));

        Assert.True(info.IsValid, info.Error);
        Assert.Equal((2, 9), (info.LfsMajor, info.LfsMinor));
        Assert.True(info.IsCompatibleWith(OsVariant.Rebase));
    }

    [Fact]
    public void Image_RejectsMissingMagic() =>
        Assert.Contains("superblock", ImageCatalog.Inspect(WriteImage(new byte[8192])).Error);

    [Fact]
    public void Image_RejectsUnknownVersion() =>
        Assert.Contains("version", ImageCatalog.Inspect(WriteImage(ImageV2(0x0003_0000))).Error);

    [Fact]
    public void Image_RejectsOversize()
    {
        var path = Path.Combine(_dir, "big.bin");
        using (var file = File.Create(path))
        {
            file.SetLength(ImageCatalog.PartitionSize + 1);
        }

        Assert.Contains("larger", ImageCatalog.Inspect(path).Error);
    }

    [Fact]
    public void Image_RejectsPathWithSpace() =>
        Assert.Contains("spaces", ImageCatalog.Inspect(WriteImage(ImageV2(), "my image.bin")).Error);

    [Fact]
    public void Image_RealImagesMatchTheirNames()
    {
        // fs-images/ is git-ignored, so this checks nothing on a fresh clone.
        foreach (var image in ImageCatalog.List(Path.Combine(RepoRoot(), "fs-images")))
        {
            Assert.True(image.IsValid, $"{image.Name}: {image.Error}");
            if (image.Name.EndsWith("-legacy.bin", StringComparison.Ordinal))
            {
                Assert.Equal(1, image.LfsMajor);
            }
            else if (image.Name.EndsWith("-rebase.bin", StringComparison.Ordinal))
            {
                Assert.Equal(2, image.LfsMajor);
            }
        }
    }

    [Fact]
    public void Tools_PathAddsUserDirsWithoutDuplicates()
    {
        var env = new ToolEnvironment("/home/u", "/usr/bin:/custom:/opt/homebrew/bin");

        Assert.Contains("/home/u/.dotnet/tools", env.SearchPath);
        Assert.Contains("/custom", env.SearchPath);
        Assert.Equal(env.SearchPath.Count, env.SearchPath.Distinct().Count());
    }

    [Fact]
    public void Tools_UserPathsComeBeforeFallbacks()
    {
        var env = new ToolEnvironment("/home/u", "/inherited:/usr/bin", "/pyenv/shims:/opt/homebrew/bin");

        Assert.Equal(["/pyenv/shims", "/opt/homebrew/bin", "/inherited", "/usr/bin"], env.SearchPath.Take(4));
    }

    [Fact]
    public void Tools_FindsOnlyExecutables()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, ".local", "bin")).FullName;
        var tool = Path.Combine(bin, "emu-test-tool");
        File.WriteAllText(tool, "#!/bin/sh\n");
        var env = new ToolEnvironment(_dir, null);

        File.SetUnixFileMode(tool, UnixFileMode.UserRead);
        Assert.Null(env.Find("emu-test-tool"));

        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Equal(tool, env.Find("emu-test-tool"));
    }
}
