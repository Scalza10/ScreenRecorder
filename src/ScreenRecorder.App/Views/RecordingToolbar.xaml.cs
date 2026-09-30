using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Media;
using ScreenRecorder.Core.Recording;
using static ScreenRecorder.App.Services.NativeMethods;

namespace ScreenRecorder.App.Views;

/// <summary>Small always-on-top controls shown while recording. Excluded from screen capture.</summary>
public partial class RecordingToolbar : Window
{
    private readonly RecordingSession _session;
    private readonly DispatcherTimer _timer;

    public RecordingToolbar(RecordingSession session, MonitorInfo monitor)
    {
        _session = session;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            // Start near the top centre of the monitor being recorded.
            SetWindowPos(hwnd, IntPtr.Zero, monitor.Bounds.X + monitor.Bounds.Width / 2 - 170, monitor.Bounds.Y + 16, 0, 0,
                SWP_NOZORDER | SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not System.Windows.Controls.Button) DragMove();
        };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Refresh(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    public event Action? PauseToggleRequested;

    public event Action? StopRequested;

    public void Refresh()
    {
        var elapsed = _session.Elapsed;
        TimeText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
        var paused = _session.State == RecordingState.Paused;
        PauseButton.Content = paused ? "Resume" : "Pause";
        StateText.Text = paused ? "PAUSED" : "REC";
        RecDot.Fill = paused ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
    }

    public void AddWarning(string warning)
    {
        WarningText.Text = WarningText.Text.Length == 0 ? warning : WarningText.Text + "\n" + warning;
        WarningText.Visibility = Visibility.Visible;
    }

    public void ShowSaving()
    {
        _timer.Stop();
        StateText.Text = "SAVING…";
        PauseButton.IsEnabled = StopButton.IsEnabled = false;
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => PauseToggleRequested?.Invoke();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
}
