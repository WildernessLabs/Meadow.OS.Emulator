using MeadowEmulator.Core;

namespace MeadowEmulator.Core.Tests;

public sealed class DeployTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("emu-ui-deploy").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void FindsBoardInSource()
    {
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_dir, "MeadowApp.cs"), "public class MeadowApp : App<F7CoreComputeV2> { }");

        Assert.Equal("F7CoreComputeV2", DeployService.FindBoard(Path.Combine(_dir, "App.csproj")));
    }

    [Fact]
    public void IgnoresBoardInBuildOutput()
    {
        var obj = Directory.CreateDirectory(Path.Combine(_dir, "obj")).FullName;
        File.WriteAllText(Path.Combine(obj, "Generated.cs"), "class A : App<F7FeatherV2> { }");

        Assert.Null(DeployService.FindBoard(_dir));
    }

    [Fact]
    public void FindsPublishDirectory()
    {
        var publish = Directory.CreateDirectory(Path.Combine(_dir, "bin", "Release", "net10.0", "publish")).FullName;

        Assert.Equal(publish, DeployService.FindPublishDirectory(_dir));
    }

    [Theory]
    [InlineData("|         ", true)]
    [InlineData("/", true)]
    [InlineData("-", true)]
    [InlineData("\\", true)]
    [InlineData("- item", false)]
    [InlineData("", false)]
    public void RecognizesSpinnerFrames(string line, bool expected) =>
        Assert.Equal(expected, DeployService.IsSpinnerFrame(line));

    [Fact]
    public void DetectsStalePublish()
    {
        var publish = Directory.CreateDirectory(Path.Combine(_dir, "bin", "Release", "net10.0", "publish")).FullName;
        var output = Path.Combine(publish, "App.dll");
        var source = Path.Combine(_dir, "MeadowApp.cs");
        File.WriteAllText(output, "x");
        File.WriteAllText(source, "x");
        var now = DateTime.UtcNow;

        File.SetLastWriteTimeUtc(source, now.AddMinutes(-1));
        File.SetLastWriteTimeUtc(output, now);
        Assert.False(DeployService.IsPublishStale(_dir, publish));

        File.SetLastWriteTimeUtc(source, now.AddMinutes(1));
        Assert.True(DeployService.IsPublishStale(_dir, publish));
    }

    [Fact]
    public void ReportsMissingPublishDirectory() =>
        Assert.Null(DeployService.FindPublishDirectory(_dir));
}
