using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScreenRecorder.Core.Gif;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.App.Views;

public partial class GifExportDialog : Window
{
    private readonly TimeSpan _duration;

    public GifExportDialog(TimeRange range, TimeSpan duration, bool videoHasEdits)
    {
        _duration = duration;
        InitializeComponent();
        StartBox.Text = TimeText.Format(range.Start);
        EndBox.Text = TimeText.Format(range.End);
        EditsNote.Visibility = videoHasEdits ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            StartBox.Focus();
            StartBox.SelectAll();
            Validate();
        };
    }

    public GifOptions? Options { get; private set; }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Validate();
    }

    private GifOptions? Validate()
    {
        GifOptions? options = null;
        string summary;
        if (!TimeText.TryParse(StartBox.Text, out var start) || !TimeText.TryParse(EndBox.Text, out var end))
        {
            summary = "Enter a start and end time.";
        }
        else if (end <= start)
        {
            summary = "The end must be after the start.";
        }
        else if (end > _duration + TimeSpan.FromMilliseconds(50))
        {
            summary = $"The video is only {TimeText.Format(_duration)} long.";
        }
        else
        {
            var fps = SelectedTag(FpsBox);
            var width = SelectedTag(WidthBox);
            options = new GifOptions(new TimeRange(start, end > _duration ? _duration : end))
            {
                Fps = fps,
                MaxWidth = width == 0 ? null : width,
            };
            var frames = (int)Math.Ceiling(options.Range.Duration.TotalSeconds * fps);
            summary = $"{options.Range.Duration.TotalSeconds:0.0} seconds · about {frames} frames" +
                      (options.Range.Duration.TotalSeconds > 30 ? " · long GIFs get very large; consider fewer fps or a smaller width" : "");
        }

        SummaryText.Text = summary;
        OkButton.IsEnabled = options is not null;
        return options;
    }

    private static int SelectedTag(ComboBox box) =>
        int.Parse((string)((ComboBoxItem)box.SelectedItem).Tag, CultureInfo.InvariantCulture);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Options = Validate();
        if (Options is not null) DialogResult = true;
    }
}
