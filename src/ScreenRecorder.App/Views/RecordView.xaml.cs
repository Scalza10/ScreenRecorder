using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Devices;
using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Recording;

namespace ScreenRecorder.App.Views;

public sealed record RecordingFile(string Path, string Name, string Kind, string Details, string Size)
{
    public override string ToString() => Name; // what screen readers announce for a list item
}

public partial class RecordView : UserControl
{
    private const string NoMicrophone = "No microphone";

    private readonly string _folder = RecordingPaths.DefaultFolder;
    private HotkeyService? _hotkeys;
    private RecordingSession? _session;
    private RecordingToolbar? _toolbar;
    private PixelRect? _region;
    private MonitorInfo? _regionMonitor;
    private bool _busy;

    public RecordView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _hotkeys?.Dispose();
    }

    public event Action<string>? OpenInEditorRequested;

    public bool IsRecording => _session is { State: RecordingState.Recording or RecordingState.Paused };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hotkeys is not null) return; // Loaded fires again when switching tabs
        _hotkeys = new HotkeyService();
        var stopOk = _hotkeys.Register(Key.F9, () => _ = StopRecordingAsync(openInEditor: true));
        var pauseOk = _hotkeys.Register(Key.F10, () => _ = TogglePauseAsync());
        if (!stopOk || !pauseOk) ShowStatus("Another app is using Ctrl+Shift+F9/F10, so those shortcuts are unavailable. Use the recording controls instead.");

        LoadMonitors();
        FolderText.Text = $"Saved in {_folder}";
        RefreshRecordings();
        await LoadMicrophonesAsync();
    }

    private void LoadMonitors()
    {
        var monitors = MonitorService.GetMonitors();
        MonitorBox.ItemsSource = monitors;
        MonitorBox.SelectedIndex = 0;
        MonitorBox.SelectionChanged += (_, _) => ClearRegion();
    }

    private async Task LoadMicrophonesAsync()
    {
        var items = new List<string> { NoMicrophone };
        try
        {
            items.AddRange(await AudioDevices.ListAsync(App.Ffmpeg));
        }
        catch (Exception ex)
        {
            ShowStatus("Could not list microphones: " + ex.Message);
        }

        MicBox.ItemsSource = items;
        MicBox.SelectedIndex = items.Count > 1 ? 1 : 0;
    }

    // ---------- Area selection ----------

    private void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorBox.SelectedItem is not MonitorInfo monitor) return;
        var window = Window.GetWindow(this)!;
        window.WindowState = WindowState.Minimized;
        try
        {
            var region = RegionSelectorWindow.Select(monitor);
            if (region is null) return;
            _region = region;
            _regionMonitor = monitor;
            RegionOption.IsChecked = true;
            RegionText.Text = $"{region.Value.Width}×{region.Value.Height} at ({region.Value.X}, {region.Value.Y})";
        }
        finally
        {
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    private void ClearRegion()
    {
        _region = null;
        _regionMonitor = null;
        RegionText.Text = "No area selected";
    }

    // ---------- Recording ----------

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecording) await StopRecordingAsync(openInEditor: true);
        else await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (_busy || MonitorBox.SelectedItem is not MonitorInfo monitor) return;
        if (RegionOption.IsChecked == true && (_region is null || _regionMonitor != monitor))
        {
            SelectRegion_Click(this, new RoutedEventArgs());
            if (_region is null) return;
        }

        var options = new RecordingOptions(monitor.Bounds)
        {
            DdagrabOutputIndex = monitor.DdagrabIndex,
            Region = RegionOption.IsChecked == true ? _region : null,
            Fps = int.Parse((string)((ComboBoxItem)FpsBox.SelectedItem).Tag),
            MicDevice = MicBox.SelectedItem is string mic && mic != NoMicrophone ? mic : null,
            DrawCursor = CursorBox.IsChecked == true,
        };

        _busy = true;
        HideStatus();
        RecordButton.IsEnabled = false;
        RecordButton.Content = "Starting…";
        var session = new RecordingSession(App.Ffmpeg, options, RecordingPaths.NewRecordingPath(_folder, DateTime.Now));
        var warnings = new List<string>();
        session.Warning += warnings.Add;
        session.Faulted += error => Dispatcher.BeginInvoke(() => OnRecordingFaulted(error));
        try
        {
            await session.StartAsync();
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            Dialogs.ShowError(this, "Could not start recording", ex);
            ResetRecordButton();
            _busy = false;
            return;
        }

        _session = session;
        if (warnings.Count > 0) ShowStatus(string.Join("\n", warnings));
        RecordButton.IsEnabled = true;
        RecordButton.Content = "■ Stop and save";

        _toolbar = new RecordingToolbar(session, monitor, warnings);
        _toolbar.PauseToggleRequested += () => _ = TogglePauseAsync();
        _toolbar.StopRequested += () => _ = StopRecordingAsync(openInEditor: true);
        Window.GetWindow(this)!.WindowState = WindowState.Minimized;
        _toolbar.Show();
        _busy = false;
    }

    private async Task TogglePauseAsync()
    {
        if (_busy || _session is null) return;
        _busy = true;
        try
        {
            if (_session.State == RecordingState.Recording) await _session.PauseAsync();
            else if (_session.State == RecordingState.Paused) await _session.ResumeAsync();
        }
        catch (Exception ex)
        {
            Dialogs.ShowError(this, "Recording", ex);
        }
        finally
        {
            _toolbar?.Refresh();
            _busy = false;
        }
    }

    public async Task StopRecordingAsync(bool openInEditor)
    {
        if (_busy || _session is null) return;
        _busy = true;
        var session = _session;
        _toolbar?.ShowSaving();
        try
        {
            var path = await session.StopAsync();
            RefreshRecordings();
            if (openInEditor) OpenInEditorRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError(this, "Could not save the recording", ex);
        }
        finally
        {
            await session.DisposeAsync();
            _session = null;
            CloseToolbarAndRestore();
            _busy = false;
        }
    }

    public async Task DiscardRecordingAsync()
    {
        if (_session is null) return;
        var session = _session;
        _session = null;
        await session.DiscardAsync();
        await session.DisposeAsync();
        CloseToolbarAndRestore();
    }

    private void OnRecordingFaulted(FfmpegException error)
    {
        _toolbar?.Refresh();
        Dialogs.ShowError(this, "Recording stopped", error);
    }

    private void CloseToolbarAndRestore()
    {
        _toolbar?.Close();
        _toolbar = null;
        ResetRecordButton();
        var window = Window.GetWindow(this);
        if (window is null) return;
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ResetRecordButton()
    {
        RecordButton.IsEnabled = true;
        RecordButton.Content = "● Start recording";
    }

    // ---------- Recordings list ----------

    public void RefreshRecordings()
    {
        Directory.CreateDirectory(_folder);
        var files = new DirectoryInfo(_folder).EnumerateFiles()
            .Where(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new RecordingFile(
                f.FullName,
                f.Name,
                f.Extension.TrimStart('.').ToUpperInvariant(),
                f.LastWriteTime.ToString("ddd d MMM yyyy, HH:mm"),
                FormatSize(f.Length)))
            .ToList();
        RecordingsList.ItemsSource = files;
        EmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    private void OpenSelected()
    {
        if (RecordingsList.SelectedItem is not RecordingFile file) return;
        if (file.Kind == "MP4") OpenInEditorRequested?.Invoke(file.Path);
        else Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true }); // view GIFs in the default viewer
    }

    private void RecordingsList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void RecordingsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelected();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "MP4 video|*.mp4", InitialDirectory = _folder };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) OpenInEditorRequested?.Invoke(dialog.FileName);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRecordings();

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;
}
