using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.Core.Tests.Ffmpeg;

public class FfmpegTimeTests
{
    [Theory]
    [InlineData(10_000, "10")]
    [InlineData(1_500, "1.5")]
    [InlineData(62_250, "62.25")]
    [InlineData(0, "0")]
    public void Formats_as_invariant_seconds(int ms, string expected)
    {
        Assert.Equal(expected, FfmpegTime.Format(TimeSpan.FromMilliseconds(ms)));
    }

    [Theory]
    [InlineData("frame=  12 fps=0.0 q=-1.0 size=N/A time=00:00:01.50 bitrate=N/A speed=2.9x", 1500)]
    [InlineData("size=     256KiB time=01:02:03.04 bitrate= 12.3kbits/s", 3723040)]
    public void Parses_progress_time(string line, int expectedMs)
    {
        Assert.True(FfmpegTime.TryParseProgress(line, out var t));
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), t);
    }

    [Theory]
    [InlineData("Input #0, lavfi, from 'testsrc2':")]
    [InlineData("frame=0 time=N/A bitrate=N/A")]
    public void Ignores_lines_without_progress(string line)
    {
        Assert.False(FfmpegTime.TryParseProgress(line, out _));
    }
}
