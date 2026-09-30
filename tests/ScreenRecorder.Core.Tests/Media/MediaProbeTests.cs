using ScreenRecorder.Core.Media;
using ScreenRecorder.Core.Tests.Infrastructure;

namespace ScreenRecorder.Core.Tests.Media;

public class MediaProbeTests
{
    [Fact]
    public void Parses_ffprobe_json()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "width": 1920, "height": 1080, "avg_frame_rate": "30000/1001" },
            { "codec_type": "audio" }
          ],
          "format": { "duration": "12.500000" }
        }
        """;

        var info = MediaProbe.Parse(json);

        Assert.Equal(TimeSpan.FromSeconds(12.5), info.Duration);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.True(info.HasAudio);
        Assert.Equal(29.97, info.FrameRate, 2);
    }

    [Fact]
    public async Task Probes_a_real_file()
    {
        var clip = await TestMedia.SixtySecondClipAsync();

        var info = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, clip);

        Assert.InRange(info.Duration.TotalSeconds, 59.9, 60.2);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.True(info.HasAudio);
    }

    [Fact]
    public async Task Detects_missing_audio()
    {
        var clip = await TestMedia.SilentClipAsync();

        var info = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, clip);

        Assert.False(info.HasAudio);
    }
}
