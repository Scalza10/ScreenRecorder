using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.App.Controls;

/// <summary>
/// The editor timeline: shows the kept part (trim), removed sections (cuts), the selection and the playhead.
/// Click or drag to seek; Shift+drag to select a range.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double TrackTop = 6;
    private const double TrackHeight = 34;
    private const double LabelTop = TrackTop + TrackHeight + 4;

    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x5A, 0x62, 0x70));
    private static readonly Brush KeptBrush = Frozen(Color.FromRgb(0x2F, 0x80, 0xED));
    private static readonly Brush CutBrush = Frozen(Color.FromArgb(0xE0, 0x3A, 0x3A, 0x3A));
    private static readonly Pen CutHatchPen = FrozenPen(Color.FromArgb(0xB0, 0xE8, 0x11, 0x23), 1.5);
    private static readonly Brush SelectionBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0xC8, 0x3D));
    private static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0xFF, 0xC8, 0x3D), 2);
    private static readonly Pen PlayheadPen = FrozenPen(Colors.White, 2);
    private static readonly Pen PlayheadOutlinePen = FrozenPen(Color.FromArgb(0x90, 0, 0, 0), 4);
    private static readonly Pen TickPen = FrozenPen(Color.FromArgb(0x80, 0x80, 0x80, 0x80), 1);

    private TimeSpan _duration;
    private TimeSpan _playhead;
    private TimeRange? _trim;
    private IReadOnlyList<TimeRange> _cuts = [];
    private TimeRange? _selection;
    private TimeSpan? _selectionAnchor;
    private bool _seeking;

    public TimelineControl()
    {
        Height = 64;
        Focusable = false;
        Cursor = Cursors.Hand;
        ToolTip = "Click or drag to move the playhead. Shift+drag to select a range.";
    }

    public event Action<TimeSpan>? SeekRequested;

    public event Action<TimeRange?>? SelectionChanged;

    public TimeSpan Duration
    {
        get => _duration;
        set { _duration = value; InvalidateVisual(); }
    }

    public TimeSpan Playhead
    {
        get => _playhead;
        set { if (_playhead == value) return; _playhead = value; InvalidateVisual(); }
    }

    public TimeRange? Trim
    {
        get => _trim;
        set { _trim = value; InvalidateVisual(); }
    }

    public IReadOnlyList<TimeRange> Cuts
    {
        get => _cuts;
        set { _cuts = value; InvalidateVisual(); }
    }

    public TimeRange? Selection
    {
        get => _selection;
        set { _selection = value; InvalidateVisual(); }
    }

    private double X(TimeSpan t) =>
        _duration <= TimeSpan.Zero ? 0 : Math.Clamp(t / _duration, 0, 1) * ActualWidth;

    private TimeSpan TimeAt(double x) =>
        ActualWidth <= 0 ? TimeSpan.Zero : _duration * Math.Clamp(x / ActualWidth, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight)); // hit-testable everywhere
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, TrackTop, width, TrackHeight), 4, 4);
        if (_duration <= TimeSpan.Zero) return;

        var trim = _trim ?? new TimeRange(TimeSpan.Zero, _duration);
        dc.DrawRectangle(KeptBrush, null, new Rect(X(trim.Start), TrackTop, Math.Max(1, X(trim.End) - X(trim.Start)), TrackHeight));

        foreach (var cut in _cuts)
        {
            var rect = new Rect(X(cut.Start), TrackTop, Math.Max(2, X(cut.End) - X(cut.Start)), TrackHeight);
            dc.DrawRectangle(CutBrush, null, rect);
            dc.PushClip(new RectangleGeometry(rect));
            for (var x = rect.Left - TrackHeight; x < rect.Right; x += 8)
                dc.DrawLine(CutHatchPen, new Point(x, rect.Bottom), new Point(x + TrackHeight, rect.Top));
            dc.Pop();
        }

        if (_selection is { } sel)
        {
            var rect = new Rect(X(sel.Start), TrackTop - 3, Math.Max(2, X(sel.End) - X(sel.Start)), TrackHeight + 6);
            dc.DrawRectangle(SelectionBrush, SelectionPen, rect);
        }

        DrawTicks(dc, width);

        var px = X(_playhead);
        dc.DrawLine(PlayheadOutlinePen, new Point(px, 0), new Point(px, TrackTop + TrackHeight + 2));
        dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, TrackTop + TrackHeight + 2));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(px - 6, 0), true, true);
            g.LineTo(new Point(px + 6, 0), true, false);
            g.LineTo(new Point(px, 8), true, false);
        }

        dc.DrawGeometry(Brushes.White, PlayheadOutlinePen, head);
        dc.DrawGeometry(Brushes.White, null, head);
    }

    private void DrawTicks(DrawingContext dc, double width)
    {
        // Pick a label spacing of at least ~80 px.
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        var seconds = _duration.TotalSeconds;
        var step = steps.FirstOrDefault(s => s / seconds * width >= 80, steps[^1]);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textBrush = (Brush?)TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray;

        for (var t = 0.0; t <= seconds + 1e-6; t += step)
        {
            var x = X(TimeSpan.FromSeconds(t));
            dc.DrawLine(TickPen, new Point(x, LabelTop - 2), new Point(x, LabelTop + 3));
            var label = new FormattedText(TimeText.Format(TimeSpan.FromSeconds(t)).Replace(".0", ""),
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, textBrush, dpi);
            var lx = Math.Clamp(x - label.Width / 2, 0, Math.Max(0, width - label.Width));
            dc.DrawText(label, new Point(lx, LabelTop + 3));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero) return;
        CaptureMouse();
        var time = TimeAt(e.GetPosition(this).X);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _selectionAnchor = time;
        }
        else
        {
            _seeking = true;
            SeekRequested?.Invoke(time);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var time = TimeAt(e.GetPosition(this).X);
        if (_seeking) SeekRequested?.Invoke(time);
        else if (_selectionAnchor is { } anchor && time != anchor)
        {
            var selection = new TimeRange(anchor < time ? anchor : time, anchor < time ? time : anchor);
            Selection = selection;
            SelectionChanged?.Invoke(selection);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _seeking = false;
        _selectionAnchor = null;
        ReleaseMouseCapture();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
