using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScreenRecorder.Core.Editing;

namespace ScreenRecorder.App.Controls;

/// <summary>
/// Sits on top of the video. Shows the crop area (everything outside it dimmed) and, in edit mode, lets the user
/// drag a new crop rectangle. Works in video pixels, accounting for the letterboxing of a Uniform-stretched video.
/// </summary>
public sealed class CropOverlay : FrameworkElement
{
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0));
    private static readonly Pen BorderPen = new(Brushes.White, 1.5) { DashStyle = new DashStyle([4, 3], 0) };

    private int _videoWidth;
    private int _videoHeight;
    private CropRect? _crop;
    private bool _isEditing;
    private Point? _anchor;
    private Rect? _dragRect;

    static CropOverlay()
    {
        DimBrush.Freeze();
        BorderPen.Freeze();
    }

    public CropOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Raised when the user finishes dragging a crop rectangle.</summary>
    public event Action<CropRect>? CropDrawn;

    public void SetVideoSize(int width, int height)
    {
        _videoWidth = width;
        _videoHeight = height;
        InvalidateVisual();
    }

    public CropRect? Crop
    {
        get => _crop;
        set { _crop = value; InvalidateVisual(); }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            IsHitTestVisible = value;
            Cursor = value ? Cursors.Cross : null;
            InvalidateVisual();
        }
    }

    /// <summary>Where the video frame is drawn inside this element (Uniform stretch, centred).</summary>
    private Rect VideoArea()
    {
        if (_videoWidth <= 0 || _videoHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return Rect.Empty;
        var scale = Math.Min(ActualWidth / _videoWidth, ActualHeight / _videoHeight);
        var w = _videoWidth * scale;
        var h = _videoHeight * scale;
        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var area = VideoArea();
        if (area.IsEmpty) return;
        if (_isEditing) dc.DrawRectangle(Brushes.Transparent, null, area); // receive mouse input over the video

        Rect? shown = _dragRect;
        if (shown is null && _crop is { } c)
        {
            var scale = area.Width / _videoWidth;
            shown = new Rect(area.X + c.X * scale, area.Y + c.Y * scale, c.Width * scale, c.Height * scale);
        }

        if (shown is not { } rect) return;
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(area), new RectangleGeometry(rect));
        dc.DrawGeometry(DimBrush, null, outside);
        dc.DrawRectangle(null, BorderPen, rect);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var area = VideoArea();
        var p = e.GetPosition(this);
        if (!area.Contains(p)) return;
        _anchor = p;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_anchor is not { } anchor) return;
        var area = VideoArea();
        var p = e.GetPosition(this);
        p = new Point(Math.Clamp(p.X, area.Left, area.Right), Math.Clamp(p.Y, area.Top, area.Bottom));
        _dragRect = new Rect(anchor, p);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var drag = _dragRect;
        _anchor = null;
        _dragRect = null;
        if (drag is not { } rect) return;

        var area = VideoArea();
        var scale = _videoWidth / area.Width;
        var x = (int)Math.Round((rect.X - area.X) * scale);
        var y = (int)Math.Round((rect.Y - area.Y) * scale);
        var w = Math.Min((int)Math.Round(rect.Width * scale), _videoWidth - x);
        var h = Math.Min((int)Math.Round(rect.Height * scale), _videoHeight - y);
        InvalidateVisual();
        if (w >= 16 && h >= 16) CropDrawn?.Invoke(new CropRect(x, y, w, h).ToEven());
    }
}
