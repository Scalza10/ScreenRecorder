using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Media;
using ScreenRecorder.Core.Tests.Infrastructure;

namespace ScreenRecorder.Core.Tests.Ffmpeg;

public class FfmpegProcessTests
{
    [Fact]
    public async Task RunAsync_reports_progress_and_success()
    {
        var output = Path.Combine(TestMedia.NewTempDir(), "out.mp4");
        var progress = new List<TimeSpan>();

        var result = await FfmpegProcess.RunAsync(TestMedia.Ffmpeg.Ffmpeg,
            ["-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=3", "-c:v", "libx264", "-preset", "ultrafast", output],
            onProgress: t => { lock (progress) progress.Add(t); });

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(output));
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task RunAsync_returns_stderr_tail_on_failure()
    {
        var result = await FfmpegProcess.RunAsync(TestMedia.Ffmpeg.Ffmpeg, ["-i", "does-not-exist.mp4"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does-not-exist.mp4", result.StderrText);
    }

    [Fact]
    public async Task EnsureSuccess_throws_FfmpegException_with_details()
    {
        var result = await FfmpegProcess.RunAsync(TestMedia.Ffmpeg.Ffmpeg, ["-i", "does-not-exist.mp4"]);

        var ex = Assert.Throws<FfmpegException>(() => result.EnsureSuccess("probe test"));
        Assert.Contains("probe test", ex.Message);
        Assert.Contains("does-not-exist.mp4", ex.Details);
    }

    [Fact]
    public void Started_processes_are_killed_if_the_app_dies()
    {
        using var process = FfmpegProcess.Start(TestMedia.Ffmpeg.Ffmpeg, ["-hide_banner", "-f", "lavfi", "-i", "anullsrc", "-f", "null", "-"]);

        Assert.True(process.IsInKillOnCloseJob);
    }

    [Fact]
    public async Task StopGracefully_finalizes_a_long_running_encode()
    {
        var output = Path.Combine(TestMedia.NewTempDir(), "stopped.mkv");
        // -re makes the endless source run in real time, like a screen capture.
        var process = FfmpegProcess.Start(TestMedia.Ffmpeg.Ffmpeg,
            ["-y", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-c:v", "libx264", "-preset", "ultrafast", output]);

        await Task.Delay(TimeSpan.FromSeconds(2));
        var result = await process.StopGracefullyAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        var info = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, output);
        Assert.InRange(info.Duration.TotalSeconds, 1.0, 4.0);
    }
}
