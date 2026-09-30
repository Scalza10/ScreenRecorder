using ScreenRecorder.Core.Gif;
using ScreenRecorder.Core.Media;
using ScreenRecorder.Core.Tests.Infrastructure;

namespace ScreenRecorder.Core.Tests.Gif;

public class GifTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Builds_a_single_pass_palette_command_for_the_range()
    {
        var options = new GifOptions(new TimeRange(S(10), S(17))) { Fps = 12, MaxWidth = 640 };

        var args = GifArgsBuilder.Build("in.mp4", "out.gif", options);

        Assert.Equal(["-ss", "10", "-t", "7", "-i", "in.mp4"], args.SkipWhile(a => a != "-ss").Take(6));
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("fps=12", filter);
        Assert.Contains("min(640,iw)", filter);
        Assert.Contains("palettegen", filter);
        Assert.Contains("paletteuse", filter);
        Assert.Equal(["-loop", "0"], args.SkipWhile(a => a != "-loop").Take(2));
        Assert.Equal("out.gif", args[^1]);
    }

    [Fact]
    public void Omits_scaling_when_no_max_width()
    {
        var args = GifArgsBuilder.Build("in.mp4", "out.gif", new GifOptions(new TimeRange(S(0), S(1))) { MaxWidth = null });

        Assert.DoesNotContain("scale='", args[args.IndexOf("-filter_complex") + 1]);
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(51, 800)]
    [InlineData(15, 8)]
    public void Rejects_invalid_options(int fps, int width)
    {
        var options = new GifOptions(new TimeRange(S(0), S(1))) { Fps = fps, MaxWidth = width };

        Assert.Throws<ArgumentException>(() => GifArgsBuilder.Build("in.mp4", "out.gif", options));
    }

    [Theory]
    [InlineData(10, 17, "Recording_2026_0m10s-0m17s.gif")]
    [InlineData(70.5, 75, "Recording_2026_1m10.5s-1m15s.gif")]
    public void Default_file_name_describes_the_range(double start, double end, string expected)
    {
        var name = GifArgsBuilder.DefaultFileName(@"C:\videos\Recording_2026.mp4", new TimeRange(S(start), S(end)));

        Assert.Equal(expected, name);
    }

    [Fact]
    public async Task Exports_seconds_10_to_17_as_gif_and_leaves_the_mp4_untouched()
    {
        var clip = await TestMedia.SixtySecondClipAsync();
        var hashBefore = TestMedia.Sha256(clip);
        var output = Path.Combine(TestMedia.NewTempDir(), "part.gif");

        await GifExporter.ExportAsync(TestMedia.Ffmpeg, clip, output,
            new GifOptions(new TimeRange(S(10), S(17))) { Fps = 10, MaxWidth = 320 });

        var gif = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, output);
        Assert.InRange(gif.Duration.TotalSeconds, 6.7, 7.3);
        Assert.Equal(320, gif.Width);
        Assert.Equal(hashBefore, TestMedia.Sha256(clip));
        var mp4 = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, clip);
        Assert.InRange(mp4.Duration.TotalSeconds, 59.9, 60.2);
    }
}
