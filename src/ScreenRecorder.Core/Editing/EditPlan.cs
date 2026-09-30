using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Editing;

/// <summary>An <see cref="EditProject"/> resolved into exactly what the export will produce.</summary>
public sealed class EditPlan
{
    private EditPlan(IReadOnlyList<TimeRange> keptSegments, CropRect? crop, double speed, MediaInfo source)
    {
        KeptSegments = keptSegments;
        Crop = crop;
        Speed = speed;
        Source = source;
    }

    public MediaInfo Source { get; }

    /// <summary>Sections of the source that survive, in order.</summary>
    public IReadOnlyList<TimeRange> KeptSegments { get; }

    public CropRect? Crop { get; }

    public double Speed { get; }

    public int OutputWidth => Crop?.Width ?? Source.Width;

    public int OutputHeight => Crop?.Height ?? Source.Height;

    public TimeSpan OutputDuration =>
        TimeSpan.FromSeconds(KeptSegments.Sum(s => s.Duration.TotalSeconds) / Speed);

    public static EditPlan Create(EditProject project)
    {
        var kept = new List<TimeRange>();
        var cursor = project.Trim.Start;
        foreach (var cut in project.Cuts)
        {
            if (cut.End <= cursor || cut.Start >= project.Trim.End) continue;
            if (cut.Start > cursor) kept.Add(new TimeRange(cursor, cut.Start));
            cursor = cut.End;
        }

        if (cursor < project.Trim.End) kept.Add(new TimeRange(cursor, project.Trim.End));
        if (kept.Count == 0) throw new InvalidOperationException("The edits remove the entire video.");

        CropRect? crop = null;
        if (project.Crop is { } requested)
        {
            var even = requested.ToEven();
            if (even.X < 0 || even.Y < 0 || even.Width < 16 || even.Height < 16 ||
                even.X + even.Width > project.Source.Width || even.Y + even.Height > project.Source.Height)
            {
                throw new InvalidOperationException("The crop area must be at least 16x16 and inside the video frame.");
            }

            crop = even;
        }

        return new EditPlan(kept, crop, project.Speed, project.Source);
    }

    /// <summary>
    /// For the preview player: where playback should continue from <paramref name="position"/> so removed sections
    /// are skipped. Returns null when the position is past the last kept section.
    /// </summary>
    public TimeSpan? SkipTo(TimeSpan position)
    {
        foreach (var segment in KeptSegments)
        {
            if (position < segment.Start) return segment.Start;
            if (segment.Contains(position)) return position;
        }

        return null;
    }
}
