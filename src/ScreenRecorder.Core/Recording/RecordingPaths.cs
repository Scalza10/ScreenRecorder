using ScreenRecorder.Core.Editing;

namespace ScreenRecorder.Core.Recording;

public static class RecordingPaths
{
    /// <summary>%USERPROFILE%\Videos\ScreenRecorder</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");

    public static string NewRecordingPath(string folder, DateTime now)
    {
        Directory.CreateDirectory(folder);
        return OutputNaming.Unique(Path.Combine(folder, $"Recording_{now:yyyy-MM-dd_HH-mm-ss}.mp4"));
    }
}
