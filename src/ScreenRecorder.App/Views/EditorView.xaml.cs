using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorder.Core.Editing;
using ScreenRecorder.Core.Gif;
using ScreenRecorder.Core.Media;
using ScreenRecorder.Core.Recording;

namespace ScreenRecorder.App.Views;

/// <summary>Plays a recording and edits it non-destructively. Exports go to new files; the original is never touched.</summary>
public partial class EditorView : UserControl
{
    private static readonly TimeSpan SkipTolerance = TimeSpan.FromMilliseconds(60);

    private readonly DispatcherTimer _timer;
    private readonly Stack<EditProject> _undo = new();
    private string? _path;
    private EditProject? _project;
    private EditPlan? _plan;
    private TimeSpan? _selStart;
    private TimeSpan? _selEnd;
    private bool _playing;
    private bool _busy;
    private bool _updatingSpeedBox;

    public EditorView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => OnTick();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) =>
        {
            Pause();
            _timer.Stop();
        };

        Media.MediaEnded += (_, _) => Pause();
        Timeline.SeekRequested += Seek;
        Timeline.SelectionChanged += selection =>
        {
            _selStart = selection?.Start;
            _selEnd = selection?.End;
            UpdateSelectionUi();
        };
        CropLayer.CropDrawn += crop =>
        {
            StopCropMode();
            Apply(_project! with { Crop = crop }, $"Cropped to {crop.Width}×{crop.Height}");
        };
        PreviewKeyDown += OnPreviewKeyDown;
        UpdateUi();
    }

    /// <summary>A file was written (edited video or GIF); the recordings list should refresh.</summary>
    public event Action? FilesChanged;

    private TimeSpan Duration => _project?.Source.Duration ?? TimeSpan.Zero;

    private TimeRange? Selection =>
        _selStart is { } s && _selEnd is { } e && e > s ? new TimeRange(s, e) : null;

    // ---------- Loading ----------

    public async Task LoadAsync(string path)
    {
        // Let a pending tab switch finish so the MediaElement is in the visual tree.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        Pause();
        StopCropMode();

        MediaInfo info;
        try
        {
            info = await MediaProbe.ProbeAsync(App.Ffmpeg, path);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError(this, "Could not open the video", ex);
            return;
        }

        Media.Close();
        _path = path;
        _project = new EditProject(info);
        _plan = EditPlan.Create(_project);
        _undo.Clear();
        _selStart = _selEnd = null;

        FileText.Text = $"{Path.GetFileName(path)}   ·   {info.Width}×{info.Height}   ·   {TimeText.Format(info.Duration)}" +
                        (info.HasAudio ? "" : "   ·   no sound");
        CropLayer.SetVideoSize(info.Width, info.Height);
        Timeline.Duration = info.Duration;
        EmptyText.Visibility = Visibility.Collapsed;

        Media.Source = new Uri(path);
        Media.Pause(); // opens the file and shows the first frame
        Media.Position = TimeSpan.Zero;

        UpdateUi();
        Focus();
    }

    private void Media_Opened(object sender, RoutedEventArgs e) => Media.Position = TimeSpan.Zero;

    private void Media_Failed(object? sender, ExceptionRoutedEventArgs e) =>
        Dialogs.ShowError(this, "Could not play the video", e.ErrorException);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MP4 video|*.mp4",
            InitialDirectory = _path is null ? RecordingPaths.DefaultFolder : Path.GetDirectoryName(_path),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = LoadAsync(dialog.FileName);
    }

    // ---------- Playback ----------

    private void OnTick()
    {
        if (_path is null || _plan is null) return;
        var position = Media.Position;

        if (_playing)
        {
            // Preview the edit: jump over cut sections and stop at the end of the trimmed range.
            var target = _plan.SkipTo(position);
            if (target is null)
            {
                Pause();
                position = _plan.KeptSegments[^1].End;
                Media.Position = position;
            }
            else if (target.Value - position > SkipTolerance)
            {
                position = target.Value;
                Media.Position = position;
            }
        }

        Timeline.Playhead = position;
        PositionText.Text = $"{TimeText.Format(position)} / {TimeText.Format(Duration)}";
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (_path is null || _plan is null) return;
        if (_plan.SkipTo(Media.Position) is null) Media.Position = _plan.KeptSegments[0].Start;
        Media.SpeedRatio = _project!.Speed;
        Media.Play();
        _playing = true;
        PlayButton.Content = "❚❚ Pause";
    }

    private void Pause()
    {
        if (_path is not null) Media.Pause();
        _playing = false;
        PlayButton.Content = "▶ Play";
    }

    private void Seek(TimeSpan time)
    {
        if (_path is null) return;
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time > Duration ? Duration : time;
        Media.Position = time;
        Timeline.Playhead = time;
        PositionText.Text = $"{TimeText.Format(time)} / {TimeText.Format(Duration)}";
    }

    // ---------- Selection ----------

    private void MarkIn_Click(object sender, RoutedEventArgs e) => MarkIn();

    private void MarkOut_Click(object sender, RoutedEventArgs e) => MarkOut();

    private void MarkIn()
    {
        if (_path is null) return;
        _selStart = Media.Position;
        if (_selEnd <= _selStart) _selEnd = null;
        UpdateSelectionUi();
    }

    private void MarkOut()
    {
        if (_path is null) return;
        _selEnd = Media.Position;
        if (_selStart >= _selEnd) _selStart = null;
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        var selection = Selection;
        Timeline.Selection = selection;
        SelectionText.Text = selection is { } s
            ? $"{TimeText.Format(s.Start)} – {TimeText.Format(s.End)}  ({s.Duration.TotalSeconds:0.0} s)"
            : _selStart is { } start ? $"From {TimeText.Format(start)} – now set the end"
            : _selEnd is { } end ? $"Until {TimeText.Format(end)} – now set the start"
            : "No selection";
        TrimButton.IsEnabled = CutButton.IsEnabled = RestoreButton.IsEnabled = selection is not null;
    }

    // ---------- Edits ----------

    private bool Apply(EditProject next, string description)
    {
        EditPlan plan;
        try
        {
            plan = EditPlan.Create(next);
        }
        catch (InvalidOperationException ex)
        {
            ShowStatus(ex.Message);
            return false;
        }

        _undo.Push(_project!);
        _project = next;
        _plan = plan;
        UpdateUi();
        ShowStatus($"{description}.   Edited video: {TimeText.Format(plan.OutputDuration)} at {plan.OutputWidth}×{plan.OutputHeight}.");
        return true;
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } s) return;
        if (Apply(_project! with { Trim = s }, $"Kept only {TimeText.Format(s.Start)} – {TimeText.Format(s.End)}")) Seek(s.Start);
    }

    private void Cut_Click(object sender, RoutedEventArgs e) => CutSelection();

    private void CutSelection()
    {
        if (Selection is not { } s) return;
        if (!Apply(_project!.AddCut(s), $"Cut {TimeText.Format(s.Start)} – {TimeText.Format(s.End)}")) return;
        _selStart = _selEnd = null;
        UpdateSelectionUi();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } s) return;
        var project = _project!;
        foreach (var cut in project.Cuts.Where(c => c.Overlaps(s)).ToList()) project = project.RemoveCut(cut);
        var trim = project.Trim;
        if (s.Start < trim.Start || s.End > trim.End)
        {
            project = project with
            {
                Trim = new TimeRange(s.Start < trim.Start ? s.Start : trim.Start, s.End > trim.End ? s.End : trim.End),
            };
        }

        Apply(project, $"Restored {TimeText.Format(s.Start)} – {TimeText.Format(s.End)}");
    }

    private void Crop_Toggled(object sender, RoutedEventArgs e)
    {
        // Checked/Unchecked (not Click) so keyboard and accessibility tools toggle crop mode too.
        var editing = CropButton.IsChecked == true && _path is not null;
        if (editing) Pause();
        else CropButton.IsChecked = false;
        CropLayer.IsEditing = editing;
        CropHint.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StopCropMode()
    {
        CropButton.IsChecked = false;
        CropLayer.IsEditing = false;
        CropHint.Visibility = Visibility.Collapsed;
    }

    private void ClearCrop_Click(object sender, RoutedEventArgs e)
    {
        StopCropMode();
        if (_project?.Crop is not null) Apply(_project with { Crop = null }, "Removed the crop");
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSpeedBox || _project is null || SpeedBox.SelectedItem is not ComboBoxItem item) return;
        var speed = double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(speed - _project.Speed) < 1e-9) return;
        if (Apply(_project with { Speed = speed }, $"Speed set to {item.Content}") && _playing) Media.SpeedRatio = speed;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _project = _undo.Pop();
        _plan = EditPlan.Create(_project);
        if (_playing) Media.SpeedRatio = _project.Speed;
        UpdateUi();
        ShowStatus("Undone.");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_project is { HasChanges: true }) Apply(new EditProject(_project.Source), "All edits removed");
    }

    // ---------- Export ----------

    private async void ExportVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _path is null || _plan is null) return;
        Pause();
        var source = _path;
        var output = OutputNaming.Unique(OutputNaming.Edited(source));
        if (!await RunExportAsync($"Saving {Path.GetFileName(output)}…", output,
                progress => VideoExporter.ExportAsync(App.Ffmpeg, source, output, _plan, progress)))
        {
            return;
        }

        FilesChanged?.Invoke();
        await LoadAsync(output);
        ShowStatus($"Saved {Path.GetFileName(output)} — now showing the edited video. The original recording is unchanged.");
    }

    private async void ExportGif_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _path is null || _project is null) return;
        Pause();

        var range = Selection ?? DefaultGifRange();
        var dialog = new GifExportDialog(range, Duration, _project.HasChanges) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Options is not { } options) return;

        var save = new SaveFileDialog
        {
            Filter = "Animated GIF|*.gif",
            InitialDirectory = Path.GetDirectoryName(_path),
            FileName = GifArgsBuilder.DefaultFileName(_path, options.Range),
        };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;

        var source = _path;
        var output = save.FileName;
        if (!await RunExportAsync($"Making {Path.GetFileName(output)}…", output,
                progress => GifExporter.ExportAsync(App.Ffmpeg, source, output, options, progress)))
        {
            return;
        }

        FilesChanged?.Invoke();
        var size = new FileInfo(output).Length / 1024.0 / 1024.0;
        ShowStatus($"GIF saved: {Path.GetFileName(output)} ({size:0.0} MB). The full video is unchanged.");
    }

    private TimeRange DefaultGifRange()
    {
        var start = Media.Position;
        if (start >= Duration - TimeSpan.FromSeconds(0.5)) start = TimeSpan.Zero;
        var end = start + TimeSpan.FromSeconds(5);
        return new TimeRange(start, end > Duration ? Duration : end);
    }

    /// <summary>Runs an export with progress. On failure, reports it and deletes the half-written output.</summary>
    private async Task<bool> RunExportAsync(string message, string output, Func<Action<double>, Task> export)
    {
        _busy = true;
        UpdateUi();
        ShowStatus(message);
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        try
        {
            await export(p => Dispatcher.BeginInvoke(() => Progress.Value = p));
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
                // Best effort; the error below is what matters.
            }

            ShowStatus("Export failed.");
            Dialogs.ShowError(this, "Export failed", ex);
            return false;
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            _busy = false;
            UpdateUi();
        }
    }

    // ---------- UI state ----------

    private void UpdateUi()
    {
        var loaded = _project is not null && !_busy;
        Timeline.Trim = _project?.Trim;
        Timeline.Cuts = _project?.Cuts ?? [];
        CropLayer.Crop = _project?.Crop;

        _updatingSpeedBox = true;
        var speed = _project?.Speed ?? 1.0;
        SpeedBox.SelectedItem = SpeedBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => Math.Abs(double.Parse((string)i.Tag, System.Globalization.CultureInfo.InvariantCulture) - speed) < 1e-9);
        _updatingSpeedBox = false;

        PlayButton.IsEnabled = CropButton.IsEnabled = SpeedBox.IsEnabled = ExportGifButton.IsEnabled = loaded;
        ClearCropButton.IsEnabled = loaded && _project!.Crop is not null;
        UndoButton.IsEnabled = loaded && _undo.Count > 0;
        ResetButton.IsEnabled = ExportVideoButton.IsEnabled = loaded && _project!.HasChanges;
        UpdateSelectionUi();
        if (!loaded) TrimButton.IsEnabled = CutButton.IsEnabled = RestoreButton.IsEnabled = false;
    }

    private void ShowStatus(string text) => StatusText.Text = text;

    // ---------- Keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_path is null || _busy || e.OriginalSource is TextBox || e.OriginalSource is ComboBox { IsDropDownOpen: true }) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.I:
                MarkIn();
                break;
            case Key.O:
                MarkOut();
                break;
            case Key.Left:
                Seek(Media.Position - TimeSpan.FromSeconds(shift ? 5 : 1));
                break;
            case Key.Right:
                Seek(Media.Position + TimeSpan.FromSeconds(shift ? 5 : 1));
                break;
            case Key.Delete:
                CutSelection();
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                Undo();
                break;
            case Key.Escape when CropLayer.IsEditing:
                StopCropMode();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
