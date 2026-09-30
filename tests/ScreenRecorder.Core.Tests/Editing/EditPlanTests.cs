using ScreenRecorder.Core.Editing;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Tests.Editing;

public class EditPlanTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    private static TimeRange R(double a, double b) => new(S(a), S(b));
    private static readonly MediaInfo Source = new(S(60), 1280, 720, HasAudio: true, FrameRate: 30);

    [Fact]
    public void No_edits_keeps_the_whole_video()
    {
        var plan = EditPlan.Create(new EditProject(Source));

        Assert.Equal([R(0, 60)], plan.KeptSegments);
        Assert.Equal(S(60), plan.OutputDuration);
        Assert.Equal((1280, 720), (plan.OutputWidth, plan.OutputHeight));
        Assert.False(new EditProject(Source).HasChanges);
    }

    [Fact]
    public void Trim_and_cuts_produce_kept_segments()
    {
        var project = new EditProject(Source) { Trim = R(5, 55) }
            .AddCut(R(20, 25))
            .AddCut(R(0, 8))      // partially before the trim: only 5-8 matters
            .AddCut(R(50, 70));   // partially after the trim

        var plan = EditPlan.Create(project);

        Assert.Equal([R(8, 20), R(25, 50)], plan.KeptSegments);
        Assert.Equal(S(37), plan.OutputDuration);
        Assert.True(project.HasChanges);
    }

    [Fact]
    public void Overlapping_cuts_are_merged()
    {
        var project = new EditProject(Source).AddCut(R(10, 20)).AddCut(R(15, 30)).AddCut(R(30, 31));

        Assert.Equal([R(10, 31)], project.Cuts);
        Assert.Equal([R(0, 10), R(31, 60)], EditPlan.Create(project).KeptSegments);
    }

    [Fact]
    public void Removing_a_cut_restores_that_section()
    {
        var project = new EditProject(Source).AddCut(R(10, 20)).AddCut(R(40, 45));

        var restored = project.RemoveCut(R(10, 20));

        Assert.Equal([R(40, 45)], restored.Cuts);
    }

    [Fact]
    public void Cutting_everything_is_rejected()
    {
        var project = new EditProject(Source) { Trim = R(10, 20) }.AddCut(R(5, 25));

        Assert.Throws<InvalidOperationException>(() => EditPlan.Create(project));
    }

    [Theory]
    [InlineData(2.0, 30)]
    [InlineData(0.5, 120)]
    [InlineData(1.5, 40)]
    public void Speed_scales_output_duration(double speed, double expectedSeconds)
    {
        var plan = EditPlan.Create(new EditProject(Source) { Speed = speed });

        Assert.Equal(expectedSeconds, plan.OutputDuration.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(5)]
    public void Speed_out_of_range_is_rejected(double speed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditProject(Source) { Speed = speed });
    }

    [Fact]
    public void Crop_is_normalized_to_even_numbers_and_sets_output_size()
    {
        var plan = EditPlan.Create(new EditProject(Source) { Crop = new CropRect(101, 51, 641, 361) });

        Assert.Equal(new CropRect(100, 50, 640, 360), plan.Crop);
        Assert.Equal((640, 360), (plan.OutputWidth, plan.OutputHeight));
    }

    [Fact]
    public void Crop_outside_the_frame_is_rejected()
    {
        var project = new EditProject(Source) { Crop = new CropRect(1000, 0, 400, 300) };

        Assert.Throws<InvalidOperationException>(() => EditPlan.Create(project));
    }

    [Fact]
    public void SkipTo_moves_the_playhead_past_removed_sections()
    {
        var plan = EditPlan.Create(new EditProject(Source) { Trim = R(5, 55) }.AddCut(R(20, 25)));

        Assert.Equal(S(5), plan.SkipTo(S(1)));
        Assert.Equal(S(10), plan.SkipTo(S(10)));
        Assert.Equal(S(25), plan.SkipTo(S(21)));
        Assert.Null(plan.SkipTo(S(55)));
    }

    [Fact]
    public void Atempo_chain_stays_within_filter_limits()
    {
        Assert.Equal(["atempo=2", "atempo=2"], ExportArgsBuilder.AtempoChain(4));
        Assert.Equal(["atempo=0.5", "atempo=0.5"], ExportArgsBuilder.AtempoChain(0.25));
        Assert.Equal(["atempo=1.5"], ExportArgsBuilder.AtempoChain(1.5));
        Assert.Empty(ExportArgsBuilder.AtempoChain(1));
    }
}
