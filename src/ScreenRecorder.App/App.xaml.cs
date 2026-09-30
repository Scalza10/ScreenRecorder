using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.App;

public partial class App : Application
{
    /// <summary>The pinned FFmpeg binaries, found once at start-up.</summary>
    public static FfmpegPaths Ffmpeg { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Escape hatch for GPU/driver trouble where windows stay blank: ScreenRecorder.exe --software-render
        if (e.Args.Contains("--software-render", StringComparer.OrdinalIgnoreCase))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        try
        {
            Ffmpeg = FfmpegLocator.Locate();
        }
        catch (FfmpegNotFoundException ex)
        {
            MessageBox.Show(ex.Message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Last line of defence: report unexpected errors instead of crashing mid-recording. (If the app does die,
        // Windows stops FFmpeg and the recorded parts are offered for recovery on the next start.)
        DispatcherUnhandledException += (_, args) =>
        {
            Dialogs.ShowError(null, "Unexpected error", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
