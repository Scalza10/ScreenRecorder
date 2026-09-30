using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Tests.Infrastructure;

namespace ScreenRecorder.Core.Tests.Ffmpeg;

public class FfmpegLocatorTests
{
    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public void Finds_binaries_next_to_the_executable()
    {
        var dir = TestMedia.NewTempDir();
        Touch(Path.Combine(dir, "ffmpeg.exe"));
        Touch(Path.Combine(dir, "ffprobe.exe"));

        var paths = FfmpegLocator.Locate(dir);

        Assert.Equal(Path.Combine(dir, "ffmpeg.exe"), paths.Ffmpeg);
        Assert.Equal(Path.Combine(dir, "ffprobe.exe"), paths.Ffprobe);
    }

    [Fact]
    public void Walks_up_to_find_repo_tools_folder()
    {
        var repo = TestMedia.NewTempDir();
        Touch(Path.Combine(repo, "tools", "ffmpeg", "ffmpeg.exe"));
        Touch(Path.Combine(repo, "tools", "ffmpeg", "ffprobe.exe"));
        var nested = Path.Combine(repo, "src", "App", "bin", "Debug", "net9.0-windows");
        Directory.CreateDirectory(nested);

        var paths = FfmpegLocator.Locate(nested);

        Assert.Equal(Path.Combine(repo, "tools", "ffmpeg", "ffmpeg.exe"), paths.Ffmpeg);
    }

    [Fact]
    public void Throws_helpful_error_when_missing()
    {
        var dir = TestMedia.NewTempDir();

        var ex = Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Locate(dir));

        Assert.Contains("setup.ps1", ex.Message);
    }

    [Fact]
    public void Default_locate_finds_the_pinned_repo_copy()
    {
        var paths = FfmpegLocator.Locate();

        Assert.True(File.Exists(paths.Ffmpeg));
        Assert.True(File.Exists(paths.Ffprobe));
    }
}
