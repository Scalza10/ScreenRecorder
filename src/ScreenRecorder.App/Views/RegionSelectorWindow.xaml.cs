using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Recording;
using static ScreenRecorder.App.Services.NativeMethods;

namespace ScreenRecorder.App.Views;

/// <summary>A dimmed full-screen overlay on one monitor where the user drags out the area to record.</summary>
public partial class RegionSelectorWindow : Window
{
    private const int MinSize = 16;

    private readonly MonitorInfo _monitor;
    private Point? _anchor;
    private PixelRect? _result;

    private RegionSelectorWindow(MonitorInfo monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // Put the window on the chosen monitor; Maximized then makes it cover exactly that monitor.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, monitor.Bounds.X + 50, monitor.Bounds.Y + 50, 200, 200, SWP_NOZORDER | SWP_NOACTIVATE);
            // The overlay itself must never end up in a recording.
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        };
        Loaded += (_, _) =>
        {
            WindowState = WindowState.Maximized;
            Activate();
            Focus();
        };
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>Shows the overlay and returns the selected area relative to the monitor, in physical pixels.</summary>
    public static PixelRect? Select(MonitorInfo monitor)
    {
        var window = new RegionSelectorWindow(monitor);
        window.ShowDialog();
        return window._result;
    }

    private void Layout()
    {
        FullArea.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        Instructions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(Instructions, (ActualWidth - Instructions.DesiredSize.Width) / 2);
        Canvas.SetTop(Instructions, 40);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _anchor = e.GetPosition(Surface);
        CaptureMouse();
        Instructions.Visibility = Visibility.Collapsed;
        UpdateSelection(_anchor.Value);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_anchor is not null) UpdateSelection(e.GetPosition(Surface));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_anchor is null) return;
        ReleaseMouseCapture();
        var region = ToPhysical(SelectionRect(e.GetPosition(Surface)));
        _anchor = null;
        if (region.Width < MinSize || region.Height < MinSize)
        {
            // A click rather than a drag: start over.
            SelectionBorder.Visibility = SizeLabel.Visibility = Visibility.Collapsed;
            Hole.Rect = Rect.Empty;
            Instructions.Visibility = Visibility.Visible;
            return;
        }

        _result = region;
        Close();
    }

    private Rect SelectionRect(Point current) => new(_anchor!.Value, current);

    private void UpdateSelection(Point current)
    {
        var rect = SelectionRect(current);
        Hole.Rect = rect;
        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        var physical = ToPhysical(rect);
        SizeText.Text = $"{physical.Width} × {physical.Height}";
        Canvas.SetLeft(SizeLabel, rect.X);
        Canvas.SetTop(SizeLabel, rect.Bottom + 6 < ActualHeight - 24 ? rect.Bottom + 6 : rect.Top - 26);
        SizeLabel.Visibility = Visibility.Visible;
    }

    /// <summary>WPF works in DPI-independent units; FFmpeg needs real pixels. The window's origin is the monitor's.</summary>
    private PixelRect ToPhysical(Rect rect)
    {
        var toDevice = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformToDevice;
        var left = (int)Math.Round(Math.Max(0, rect.Left) * toDevice.M11);
        var top = (int)Math.Round(Math.Max(0, rect.Top) * toDevice.M22);
        var right = (int)Math.Round(Math.Min(ActualWidth, rect.Right) * toDevice.M11);
        var bottom = (int)Math.Round(Math.Min(ActualHeight, rect.Bottom) * toDevice.M22);
        right = Math.Min(right, _monitor.Bounds.Width);
        bottom = Math.Min(bottom, _monitor.Bounds.Height);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)).ToEven();
    }
}
