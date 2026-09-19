using System.Globalization;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using videoclipper.Models;
using videoclipper.Services;

namespace videoclipper.Views;

// Trim and output settings, and encoding.
public partial class MainWindow
{
    private CancellationTokenSource? _encodeCancel;

    private bool IsEncoding => _encodeCancel is not null;

    private void InitializeEncode()
    {
        StartSpin.ValueChanged += (_, _) => TrimChanged();
        EndSpin.ValueChanged += (_, _) => TrimChanged();
        SizeSpin.ValueChanged += (_, _) => TrimChanged();
        QualitySpin.ValueChanged += (_, _) => TrimChanged();
        FormatCombo.SelectionChanged += (_, _) => FormatChanged();
        AudioCombo.SelectionChanged += (_, _) => AudioChanged();
    }

    private string VideoFormat() => SelectedTag(FormatCombo) ?? VideoCore.Mp4;

    private int TheoraQuality() => (int)Value(QualitySpin);

    /// <summary>Updates the duration, the timeline's IN / OUT markers and the bitrate estimate.</summary>
    private void TrimChanged()
    {
        var duration = Value(EndSpin) - Value(StartSpin);
        ClipText.Text = $"Selected duration: {VideoCore.FormatTime(Math.Max(0, duration))}";
        if (_info is not null)
            Timeline.SetTrim(Value(StartSpin), Value(EndSpin));
        if (_info is null || duration <= 0)
        {
            BitrateText.Text = "";
            return;
        }
        if (VideoFormat() == VideoCore.Ogv)
        {
            BitrateText.Text = VideoCore.DescribeTheoraQuality(TheoraQuality());
            return;
        }
        try
        {
            var audio = int.Parse(SelectedTag(AudioCombo) ?? "128");
            var includeAudio = _info.HasAudio && audio > 0;
            var (_, outputSize) = CropSettings();
            var bitrate = VideoCore.CalculateVideoBitrateKbps(duration, Value(SizeSpin), audio, includeAudio, outputSize);
            var estimate = VideoCore.EstimatedSizeMb(duration, bitrate, audio, includeAudio);
            BitrateText.Text = string.Format(CultureInfo.CurrentCulture,
                "Video bitrate: {0} kbps • estimated output: {1:0.0} MB", bitrate, estimate);
        }
        catch (VideoException ex)
        {
            BitrateText.Text = ex.Message;
        }
    }

    /// <summary>Shows the controls that apply to the selected format and hides the rest.</summary>
    private void ApplyFormat()
    {
        var isOgv = VideoFormat() == VideoCore.Ogv;
        // Theora targets a quality, not a size, so the megabyte limit and the estimate it feeds
        // have nothing to say about an OGV clip.
        SizeSpin.IsVisible = !isOgv;
        SuggestSizeButton.IsVisible = !isOgv;
        QualitySpin.IsVisible = isOgv;
        FormatNote.IsVisible = isOgv;
        if (isOgv)
        {
            SizeLabel.Text = "Quality";
            SizeUnitText.Text = $"{VideoCore.MinTheoraQuality} = smallest file, {VideoCore.MaxTheoraQuality} = best detail (single pass)";
            OutputTitle.Text = "OGV output";
            EncodeLabel.Text = "Create OGV";
        }
        else
        {
            SizeLabel.Text = "Maximum size";
            SizeUnitText.Text = "MB (decimal; 5% safety margin)";
            OutputTitle.Text = "Web MP4 output";
            EncodeLabel.Text = "Create web MP4";
        }
        TrimChanged();
    }

    /// <summary>Sets the maximum size to what the clip needs for good quality at its output size.</summary>
    private async void SuggestSize_Click(object? sender, RoutedEventArgs e)
    {
        if (_info is null)
            return;
        var duration = Value(EndSpin) - Value(StartSpin);
        var audio = int.Parse(SelectedTag(AudioCombo) ?? "128");
        var includeAudio = _info.HasAudio && audio > 0;
        double suggested;
        FrameSize outputSize;
        try
        {
            var (_, cropSize) = CropSettings();
            outputSize = cropSize ?? VideoCore.ScaledOutputSize(
                _info.Width, _info.Height, int.Parse(SelectedTag(WidthCombo) ?? "1920"));
            suggested = VideoCore.SuggestedSizeMb(duration, outputSize, _info.FrameRate, audio, includeAudio);
        }
        catch (VideoException ex)
        {
            await ShowErrorAsync(ex.Message);
            return;
        }

        var maximum = (double)SizeSpin.Maximum;
        SetValue(SizeSpin, Math.Min(suggested, maximum));
        TrimChanged();
        var videoKbps = VideoCore.GoodQualityVideoKbps(outputSize, _info.FrameRate);
        var basis = string.Format(CultureInfo.CurrentCulture,
            "Suggested for {0}\u00D7{1} at {2:0.##} fps: {3} kbps video", outputSize.Width, outputSize.Height,
            _info.FrameRate, videoKbps);
        if (includeAudio)
            basis += $" + {audio} kbps audio";
        if (suggested > maximum)
            basis += string.Format(CultureInfo.CurrentCulture, " (needs {0:0.0} MB, more than the {1:0} MB limit)", suggested, maximum);
        BitrateText.Text += "\n" + basis;
    }

    private void FormatChanged()
    {
        ApplyFormat();
        RetargetOutputExtension();
    }

    /// <summary>Points an output name that ends in .mp4 or .ogv at the selected format's extension.</summary>
    private void RetargetOutputExtension()
    {
        var current = OutputBox.Text?.Trim() ?? "";
        if (current.Length == 0)
            return;
        var extension = Path.GetExtension(current);
        if (VideoCore.VideoFormats.Any(f => string.Equals("." + f, extension, StringComparison.OrdinalIgnoreCase)))
            OutputBox.Text = current[..^extension.Length] + VideoCore.OutputExtension(VideoFormat());
    }

    private void AudioChanged()
    {
        var audio = int.Parse(SelectedTag(AudioCombo) ?? "0");
        var includeAudio = _info is { HasAudio: true } && audio > 0;
        AudioFadeCheck.IsEnabled = includeAudio;
        if (!includeAudio)
            AudioFadeCheck.IsChecked = false;
        TrimChanged();
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var extension = VideoCore.OutputExtension(VideoFormat());
        var options = new FilePickerSaveOptions
        {
            Title = "Save web video",
            SuggestedFileName = "video-web" + extension,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType($"{extension.TrimStart('.').ToUpperInvariant()} video") { Patterns = ["*" + extension] },
            ],
        };
        var current = OutputBox.Text?.Trim() ?? "";
        if (current.Length > 0)
        {
            var full = Path.GetFullPath(ExpandHome(current));
            options.SuggestedFileName = Path.GetFileName(full);
            if (Path.GetDirectoryName(full) is { } folder && Directory.Exists(folder))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder);
        }

        var file = await StorageProvider.SaveFilePickerAsync(options);
        if (file?.TryGetLocalPath() is not { } path)
            return;
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            path += extension;
        OutputBox.Text = path;
    }

    private async void Encode_Click(object? sender, RoutedEventArgs e)
    {
        if (IsEncoding || _inputPath is null || _info is null)
            return;
        if (!FFmpeg.IsInstalled("ffmpeg"))
        {
            await ShowErrorAsync($"ffmpeg was not found. {FFmpeg.InstallHint}");
            return;
        }
        var text = OutputBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            await ShowErrorAsync("Choose an output filename first.");
            return;
        }
        var output = Path.GetFullPath(ExpandHome(text));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(output, Path.GetFullPath(_inputPath), comparison))
        {
            await ShowErrorAsync("The output must be different from the input video.");
            return;
        }
        if (!Directory.Exists(Path.GetDirectoryName(output)))
        {
            await ShowErrorAsync("The output folder does not exist.");
            return;
        }

        var audio = int.Parse(SelectedTag(AudioCombo) ?? "128");
        var includeAudio = _info.HasAudio && audio > 0;
        EncodeOptions options;
        try
        {
            var (crop, outputSize) = CropSettings();
            options = new EncodeOptions(
                InputPath: _inputPath,
                OutputPath: output,
                Start: Value(StartSpin),
                End: Value(EndSpin),
                TargetMb: Value(SizeSpin),
                AudioKbps: audio,
                MaxWidth: int.Parse(SelectedTag(WidthCombo) ?? "1920"),
                HasAudio: includeAudio,
                FadeAudio: includeAudio && AudioFadeCheck.IsChecked == true,
                FadeVideo: VideoFadeCheck.IsChecked == true,
                Crop: crop,
                OutputSize: outputSize,
                VideoFormat: VideoFormat(),
                TheoraQuality: TheoraQuality());
            // Built once here to check the settings; the encoder uses its own temporary pass log.
            VideoCore.BuildEncodeCommands(options, Path.Combine(Path.GetTempPath(), "videoclipper-check"));
        }
        catch (VideoException ex)
        {
            await ShowErrorAsync(ex.Message);
            return;
        }

        StopPlayback();
        var cancel = new CancellationTokenSource();
        _encodeCancel = cancel;
        EncodeButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        EncodeProgress.Value = 0;
        ProgressText.Text = "Starting…";
        LogBox.Text = "";

        var encoder = new ClipEncoder
        {
            Status = status => Dispatcher.UIThread.Post(() => ProgressText.Text = status),
            Progress = fraction => Dispatcher.UIThread.Post(() => EncodeProgress.Value = fraction),
            Log = line => Dispatcher.UIThread.Post(() => AppendLog(line)),
        };
        bool success;
        string message;
        try
        {
            message = await Task.Run(() => encoder.EncodeAsync(options, cancel.Token));
            success = true;
        }
        catch (OperationCanceledException)
        {
            (success, message) = (false, "Encoding cancelled.");
        }
        catch (Exception ex) when (ex is VideoException or IOException or UnauthorizedAccessException)
        {
            (success, message) = (false, ex.Message);
        }
        finally
        {
            _encodeCancel = null;
            cancel.Dispose();
        }

        EncodeButton.IsEnabled = _info is not null;
        CancelButton.IsEnabled = false;
        EncodeProgress.Value = success ? 1.0 : 0.0;
        ProgressText.Text = success ? "Complete" : "Stopped";
        if (IsVisible)
        {
            await MessageDialog.ShowAsync(this, success ? "Video created" : "Encoding did not complete",
                message, isError: !success);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _encodeCancel?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancelling…";
    }

    /// <summary>Expands a leading "~" to the home folder, as a shell would.</summary>
    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return path;
    }
}
