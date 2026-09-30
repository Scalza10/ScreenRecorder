namespace ScreenRecorder.Core.Media;

/// <summary>A half-open time interval [Start, End) within a video.</summary>
public readonly record struct TimeRange
{
    public TimeRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero) throw new ArgumentException("Start cannot be negative.", nameof(start));
        if (end <= start) throw new ArgumentException("End must be after start.", nameof(end));
        Start = start;
        End = end;
    }

    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public TimeSpan Duration => End - Start;

    public bool Contains(TimeSpan time) => time >= Start && time < End;

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public TimeRange ClampTo(TimeSpan duration) => new(Start, End > duration ? duration : End);

    public override string ToString() => $"{Start:mm\\:ss\\.ff}–{End:mm\\:ss\\.ff}";
}
