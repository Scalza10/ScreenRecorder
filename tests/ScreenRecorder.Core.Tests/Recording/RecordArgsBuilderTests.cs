using ScreenRecorder.Core.Recording;

namespace ScreenRecorder.Core.Tests.Recording;

public class RecordArgsBuilderTests
{
    private static readonly PixelRect Monitor = new(1920, 0, 2560, 1440);

    private static string After(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

    [Fact]
    public void Ddagrab_full_monitor()
    {
        var options = new RecordingOptions(Monitor) { DdagrabOutputIndex = 1 };

        var args = RecordArgsBuilder.Build(options, CaptureBackend.Ddagrab, includeMic: false, "seg.mkv");

        Assert.Equal("lavfi", After(args, "-f"));
        Assert.Equal("ddagrab=output_idx=1:framerate=30:draw_mouse=1:dup_frames=1", After(args, "-i"));
        Assert.StartsWith("hwdownload,format=bgra,", After(args, "-vf"));
        Assert.Equal("seg.mkv", args[^1]);
        Assert.DoesNotContain("dshow", args);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void Ddagrab_region_uses_monitor_relative_even_offsets()
    {
        var options = new RecordingOptions(Monitor) { DdagrabOutputIndex = 0, Region = new PixelRect(101, 51, 641, 361), Fps = 60 };

        var args = RecordArgsBuilder.Build(options, CaptureBackend.Ddagrab, includeMic: false, "seg.mkv");

        Assert.Equal("ddagrab=output_idx=0:framerate=60:draw_mouse=1:dup_frames=1:offset_x=100:offset_y=50:video_size=640x360",
            After(args, "-i"));
    }

    [Fact]
    public void Gdigrab_region_uses_virtual_desktop_coordinates()
    {
        var options = new RecordingOptions(Monitor) { Region = new PixelRect(100, 50, 640, 360), DrawCursor = false };

        var args = RecordArgsBuilder.Build(options, CaptureBackend.Gdigrab, includeMic: false, "seg.mkv");

        Assert.Equal("gdigrab", After(args, "-f"));
        Assert.Equal("2020", After(args, "-offset_x"));
        Assert.Equal("50", After(args, "-offset_y"));
        Assert.Equal("640x360", After(args, "-video_size"));
        Assert.Equal("0", After(args, "-draw_mouse"));
        Assert.Equal("desktop", After(args, "-i"));
        Assert.DoesNotContain("hwdownload", After(args, "-vf"));
    }

    [Fact]
    public void Microphone_adds_dshow_input_and_aac()
    {
        var options = new RecordingOptions(Monitor) { DdagrabOutputIndex = 0, MicDevice = "Microphone Array (Realtek(R) Audio)" };

        var args = RecordArgsBuilder.Build(options, CaptureBackend.Ddagrab, includeMic: true, "seg.mkv");

        Assert.Contains("dshow", args);
        Assert.Contains("audio=Microphone Array (Realtek(R) Audio)", args);
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal(["-map", "0:v", "-map", "1:a"], args.SkipWhile(a => a != "-map").Take(4));
    }

    [Fact]
    public void Mic_is_skipped_when_not_included_even_if_configured()
    {
        var options = new RecordingOptions(Monitor) { DdagrabOutputIndex = 0, MicDevice = "Mic" };

        var args = RecordArgsBuilder.Build(options, CaptureBackend.Ddagrab, includeMic: false, "seg.mkv");

        Assert.DoesNotContain("dshow", args);
    }

    [Theory]
    [InlineData(-1, 0, 100, 100)]
    [InlineData(2500, 0, 100, 100)]
    [InlineData(0, 0, 8, 100)]
    public void Region_outside_monitor_is_rejected(int x, int y, int w, int h)
    {
        var options = new RecordingOptions(Monitor) { Region = new PixelRect(x, y, w, h) };

        Assert.Throws<ArgumentException>(() => RecordArgsBuilder.Build(options, CaptureBackend.Gdigrab, false, "seg.mkv"));
    }

    [Fact]
    public void Ddagrab_requires_an_output_index()
    {
        var options = new RecordingOptions(Monitor);

        Assert.Throws<InvalidOperationException>(() => RecordArgsBuilder.Build(options, CaptureBackend.Ddagrab, false, "seg.mkv"));
    }
}
