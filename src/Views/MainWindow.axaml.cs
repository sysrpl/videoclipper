using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using videoclipper.Models;
using videoclipper.Services;

namespace videoclipper.Views;

/// <summary>
/// The video trimmer: open a recording, pick the start and end on the timeline, optionally crop
/// it, and encode a web MP4 or a Godot OGV with ffmpeg. The parts live in partial files: Preview
/// (frames, playback, seeking), Crop (the Cropping tab) and Encode (output settings and encoding).
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] VideoPatterns = ["*.mkv", "*.mp4", "*.mov", "*.avi", "*.webm", "*.m4v"];

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private string? _inputPath;
    private VideoInfo? _info;

    /// <summary>For the XAML designer.</summary>
    public MainWindow() : this(new SettingsService())
    {
    }

    public MainWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        InitializeComponent();

        // Saved values first, before the change handlers are attached.
        SelectTag(FormatCombo, VideoCore.VideoFormats.Contains(_settings.VideoFormat) ? _settings.VideoFormat : VideoCore.Mp4);
        SetValue(SizeSpin, Math.Clamp(_settings.MaximumSizeMb, 0.1, 1000));
        SetValue(QualitySpin, Math.Clamp(_settings.TheoraQuality, VideoCore.MinTheoraQuality, VideoCore.MaxTheoraQuality));
        SelectTag(AudioCombo, _settings.AudioKbps is 0 or 96 or 128 or 160 or 192 ? _settings.AudioKbps.ToString() : "128");

        InitializePreview();
        InitializeCrop();
        InitializeEncode();

        KeyDown += Window_KeyDown;
        Closing += Window_Closing;
        ApplyFormat();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            Open_Click(sender, e);
        }
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        SaveSettings();
        _encodeCancel?.Cancel();
        _decoder?.Dispose();
        _decoder = null;
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                // Upper case too: file name patterns are case-sensitive on Linux.
                new FilePickerFileType("Video files")
                {
                    Patterns = [.. VideoPatterns, .. VideoPatterns.Select(p => p.ToUpperInvariant())],
                },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null)
            await LoadVideoAsync(path);
    }

    private async void About_Click(object? sender, RoutedEventArgs e) =>
        await new AboutWindow().ShowModalAsync(this);

    private async Task LoadVideoAsync(string path)
    {
        VideoInfo info;
        try
        {
            info = await VideoCore.ProbeVideoAsync(path);
        }
        catch (VideoException ex)
        {
            await ShowErrorAsync(ex.Message);
            return;
        }
        if (_info is not null)
            SaveSettings();

        _inputPath = path;
        _info = info;
        FileText.Text = $"{Path.GetFileName(path)} — {info.Width}x{info.Height}, {VideoCore.FormatTime(info.Duration)}";
        Timeline.SetMedia(info.Duration, info.FrameRate);
        StartSpin.Maximum = (decimal)info.Duration;
        EndSpin.Maximum = (decimal)info.Duration;
        SetValue(StartSpin, 0);
        SetValue(EndSpin, info.Duration);
        OutputBox.Text = Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            Path.GetFileNameWithoutExtension(path) + "-web" + VideoCore.OutputExtension(VideoFormat()));
        if (!info.HasAudio)
            SelectTag(AudioCombo, "0");
        AudioCombo.IsEnabled = info.HasAudio;
        AudioChanged();
        CropOverlay.SetVideoSize(info.Width, info.Height);
        CropCheck.IsEnabled = info.Width > 1 && info.Height > 1;
        if (!CropCheck.IsEnabled)
            CropCheck.IsChecked = false;
        RestoreThumbnailSettings();
        PlayButton.IsEnabled = true;
        SuggestSizeButton.IsEnabled = true;
        EncodeButton.IsEnabled = !IsEncoding;
        OpenPreview(path, info);
        TrimChanged();
    }

    /// <summary>Remembers the output and thumbnail values for the next session.</summary>
    private void SaveSettings()
    {
        _settings.MaximumSizeMb = Math.Round(Value(SizeSpin), 1);
        _settings.TheoraQuality = TheoraQuality();
        _settings.VideoFormat = VideoFormat();
        _settings.AudioKbps = int.Parse(SelectedTag(AudioCombo) ?? "0");
        _settings.CropEnabled = CropCheck.IsChecked == true;
        _settings.OutputSize = new FrameSize((int)Value(OutputWidthSpin), (int)Value(OutputHeightSpin));
        _settings.OutputSizeDriver = _outputSizeDriver;
        _settings.PreviewHeight = PreviewRow.Height.Value;
        if (_info is not null)
            _settings.CropBounds = CropValues();
        _settingsService.Save(_settings);
    }

    private Task ShowErrorAsync(string message) =>
        MessageDialog.ShowAsync(this, "Unable to continue", message, isError: true);

    private void AppendLog(string text)
    {
        LogBox.Text += text;
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private static double Value(NumericUpDown spin) => (double)(spin.Value ?? 0m);

    private static void SetValue(NumericUpDown spin, double value)
    {
        if (double.IsFinite(value))
            spin.Value = (decimal)value;
    }

    private static string? SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void SelectTag(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag))
                             ?? combo.SelectedItem;
}
