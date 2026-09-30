using System.Windows;
using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.App;

internal static class Dialogs
{
    public static void ShowError(DependencyObject? owner, string title, Exception error, string? hint = null)
    {
        var text = error.Message;
        if (hint is not null) text += "\n\n" + hint;
        if (error is FfmpegException { Details.Length: > 0 } ffmpeg) text += "\n\nDetails from FFmpeg:\n" + ffmpeg.Details;
        Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static MessageBoxResult Show(DependencyObject? owner, string text, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var window = owner is null ? Application.Current.MainWindow : Window.GetWindow(owner);
        return window is { IsVisible: true }
            ? MessageBox.Show(window, text, title, buttons, icon)
            : MessageBox.Show(text, title, buttons, icon);
    }
}
