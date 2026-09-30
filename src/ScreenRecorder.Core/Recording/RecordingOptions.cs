namespace ScreenRecorder.Core.Recording;

/// <summary>A rectangle in physical (not DPI-scaled) screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>H.264 in yuv420p needs even sizes; offsets are kept even too so chroma lines up.</summary>
    public PixelRect ToEven() => new(X & ~1, Y & ~1, Width & ~1, Height & ~1);
}

public enum CaptureBackend
{
    /// <summary>Desktop Duplication API: GPU-based, efficient, captures in physical pixels.</summary>
    Ddagrab,

    /// <summary>GDI BitBlt: slower, but works with any adapter and over remote desktop.</summary>
    Gdigrab,
}

/// <param name="MonitorBounds">The monitor being recorded, in virtual-desktop physical pixels.</param>
public sealed record RecordingOptions(PixelRect MonitorBounds)
{
    /// <summary>
    /// The monitor's DXGI output index on the default GPU (what ddagrab calls output_idx), or null when the
    /// monitor is driven by another adapter; then only GDI capture is possible.
    /// </summary>
    public int? DdagrabOutputIndex { get; init; }

    /// <summary>Part of the monitor to record, relative to the monitor's top-left corner. Null = whole monitor.</summary>
    public PixelRect? Region { get; init; }

    public int Fps { get; init; } = 30;

    /// <summary>DirectShow audio device name, or null for no microphone.</summary>
    public string? MicDevice { get; init; }

    public bool DrawCursor { get; init; } = true;
}
