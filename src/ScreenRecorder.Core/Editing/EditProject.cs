using System.Collections.Immutable;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Editing;

/// <summary>A crop rectangle in source-video pixels.</summary>
public readonly record struct CropRect(int X, int Y, int Width, int Height)
{
    /// <summary>H.264 in yuv420p needs even sizes and offsets.</summary>
    public CropRect ToEven() => new(X & ~1, Y & ~1, Width & ~1, Height & ~1);
}

/// <summary>
/// A non-destructive list of edits to apply to a source video. Immutable: every change returns a new project,
/// which makes undo trivial and keeps the UI and export in sync.
/// </summary>
public sealed record EditProject(MediaInfo Source)
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4.0;

    private readonly double _speed = 1.0;

    /// <summary>The part of the source to keep (defaults to all of it).</summary>
    public TimeRange Trim { get; init; } = new(TimeSpan.Zero, Source.Duration);

    /// <summary>Sections removed from the middle, sorted and non-overlapping.</summary>
    public ImmutableList<TimeRange> Cuts { get; private init; } = [];

    public CropRect? Crop { get; init; }

    public double Speed
    {
        get => _speed;
        init
        {
            if (value is < MinSpeed or > MaxSpeed)
                throw new ArgumentOutOfRangeException(nameof(Speed), $"Speed must be between {MinSpeed}x and {MaxSpeed}x.");
            _speed = value;
        }
    }

    public bool HasChanges =>
        Trim != new TimeRange(TimeSpan.Zero, Source.Duration) || !Cuts.IsEmpty || Crop is not null || Speed != 1.0;

    public EditProject AddCut(TimeRange cut)
    {
        var merged = new List<TimeRange>();
        var pending = cut;
        foreach (var existing in Cuts)
        {
            if (existing.Overlaps(pending) || existing.End == pending.Start || pending.End == existing.Start)
            {
                pending = new TimeRange(Min(existing.Start, pending.Start), Max(existing.End, pending.End));
            }
            else
            {
                merged.Add(existing);
            }
        }

        merged.Add(pending);
        return this with { Cuts = [.. merged.OrderBy(c => c.Start)] };
    }

    public EditProject RemoveCut(TimeRange cut) => this with { Cuts = Cuts.Remove(cut) };

    public EditProject ClearCuts() => this with { Cuts = [] };

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
