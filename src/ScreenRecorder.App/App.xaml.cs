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

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
