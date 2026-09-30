using ScreenRecorder.Core.Editing;
using ScreenRecorder.Core.Media;
using ScreenRecorder.Core.Tests.Infrastructure;

namespace ScreenRecorder.Core.Tests.Editing;

public class ExportTests
{
    private static TimeRange R(double a, double b) => new(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b));

    [Fact]
    public async Task Exports_trim_cut_crop_and_speed_without_touching_the_original()
    {
        var clip = await TestMedia.SixtySecondClipAsync();
        var hashBefore = TestMedia.Sha256(clip);
        var source = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, clip);
        var project = new EditProject(source)
        {
            Trim = R(5, 55),
            Crop = new CropRect(100, 50, 640, 360),
            Speed = 1.5,
        }.AddCut(R(20, 25));
        var plan = EditPlan.Create(project);
        var output = Path.Combine(TestMedia.NewTempDir(), "edited.mp4");
        var progress = new List<double>();

        await VideoExporter.ExportAsync(TestMedia.Ffmpeg, clip, output, plan, p => { lock (progress) progress.Add(p); });

        var result = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, output);
        Assert.Equal(30, plan.OutputDuration.TotalSeconds, 3);
        Assert.InRange(result.Duration.TotalSeconds, 29.7, 30.3);
        Assert.Equal(640, result.Width);
        Assert.Equal(360, result.Height);
        Assert.True(result.HasAudio);
        Assert.InRange(result.FrameRate, 29.9, 30.1);
        Assert.NotEmpty(progress);
        Assert.Equal(hashBefore, TestMedia.Sha256(clip));
    }

    [Fact]
    public async Task Exports_a_video_without_audio()
    {
        var clip = await TestMedia.SilentClipAsync();
        var source = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, clip);
        var plan = EditPlan.Create(new EditProject(source).AddCut(R(1, 3)));
        var output = Path.Combine(TestMedia.NewTempDir(), "edited.mp4");

        await VideoExporter.ExportAsync(TestMedia.Ffmpeg, clip, output, plan);

        var result = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, output);
        Assert.InRange(result.Duration.TotalSeconds, 3.8, 4.2);
        Assert.False(result.HasAudio);
    }

    [Fact]
    public void Output_names_never_overwrite_existing_files()
    {
        var dir = TestMedia.NewTempDir();
        var source = Path.Combine(dir, "Recording.mp4");

        var first = OutputNaming.Unique(OutputNaming.Edited(source));
        File.WriteAllText(first, "");
        var second = OutputNaming.Unique(OutputNaming.Edited(source));

        Assert.Equal(Path.Combine(dir, "Recording_edited.mp4"), first);
        Assert.Equal(Path.Combine(dir, "Recording_edited (2).mp4"), second);
    }
}
