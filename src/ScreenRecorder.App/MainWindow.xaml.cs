using System.ComponentModel;
using System.Windows;

namespace ScreenRecorder.App;

public partial class MainWindow : Window
{
    private bool _closingAfterStop;

    public MainWindow()
    {
        InitializeComponent();
        Recorder.OpenInEditorRequested += OpenInEditor;
        Editor.FilesChanged += Recorder.RefreshRecordings;
    }

    public async void OpenInEditor(string path)
    {
        Tabs.SelectedItem = EditorTab;
        await Editor.LoadAsync(path);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingAfterStop) return;
        if (Recorder.IsBusy)
        {
            e.Cancel = true;
            Dialogs.Show(this, "The recording is starting, pausing or being saved. Please try again in a moment.", "Screen Recorder");
            return;
        }

        if (!Recorder.IsRecording) return;

        e.Cancel = true;
        var answer = Dialogs.Show(this, "A recording is in progress. Save it before closing?", "Screen Recorder",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        if (answer == MessageBoxResult.Yes)
        {
            if (!await Recorder.StopRecordingAsync(openInEditor: false)) return; // saving failed: stay open to retry
        }
        else
        {
            await Recorder.DiscardRecordingAsync();
        }

        _closingAfterStop = true;
        Close();
    }
}
