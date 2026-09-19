using System.Globalization;
using Avalonia.Interactivity;
using videoclipper.Models;
using videoclipper.Services;

namespace videoclipper.Views;

// The Cropping tab: the crop rectangle (on the preview and as numbers) and the output size.
public partial class MainWindow
{
    // Set while code changes the spin boxes, so their change handlers don't react.
    private bool _cropSyncing;
    private bool _outputSyncing;
    /// <summary>"width" or "height": the output side the user last changed; the other follows it.</summary>
    private string _outputSizeDriver = "width";

    private void InitializeCrop()
    {
        CropCheck.IsCheckedChanged += (_, _) => CropToggled();
        foreach (var spin in new[] { CropLeftSpin, CropTopSpin, CropWidthSpin, CropHeightSpin })
            spin.ValueChanged += (_, _) => CropSpinChanged();
        OutputWidthSpin.ValueChanged += (_, _) => OutputWidthChanged();
        OutputHeightSpin.ValueChanged += (_, _) => OutputHeightChanged();
        CropOverlay.CropChanged += OverlayCropChanged;
    }

    private void CropToggled()
    {
        var enabled = CropCheck.IsChecked == true && _info is not null;
        CropBody.IsEnabled = enabled;
        WidthCombo.IsEnabled = !enabled;
        CropOverlay.IsVisible = enabled;
        UpdateCropSummary();
    }

    private CropRect CropValues() => new(
        (int)Value(CropLeftSpin), (int)Value(CropTopSpin), (int)Value(CropWidthSpin), (int)Value(CropHeightSpin));

    private void SetCropSpins(CropRect crop)
    {
        _cropSyncing = true;
        try
        {
            SetValue(CropLeftSpin, crop.X);
            SetValue(CropTopSpin, crop.Y);
            SetValue(CropWidthSpin, crop.Width);
            SetValue(CropHeightSpin, crop.Height);
        }
        finally
        {
            _cropSyncing = false;
        }
    }

    private void ResetCrop_Click(object? sender, RoutedEventArgs e) => ResetCrop();

    /// <summary>Selects the whole frame, and sets the spin boxes' ranges for the video's size.</summary>
    private void ResetCrop()
    {
        if (_info is null)
            return;
        int width = _info.Width, height = _info.Height;
        _cropSyncing = true;
        try
        {
            CropLeftSpin.Maximum = Math.Max(0, width - VideoCore.MinCropSize);
            CropTopSpin.Maximum = Math.Max(0, height - VideoCore.MinCropSize);
            CropWidthSpin.Maximum = Math.Max(VideoCore.MinCropSize, width);
            CropHeightSpin.Maximum = Math.Max(VideoCore.MinCropSize, height);
            CropWidthSpin.Minimum = VideoCore.MinCropSize;
            CropHeightSpin.Minimum = VideoCore.MinCropSize;
        }
        finally
        {
            _cropSyncing = false;
        }
        SetCropSpins(new CropRect(0, 0, width, height));
        ApplyCrop(updateOverlay: width > 0 && height > 0);
    }

    /// <summary>Rounds the spin box values to a valid rectangle and mirrors it on the preview.</summary>
    private void ApplyCrop(bool updateOverlay = true)
    {
        if (_info is null)
            return;
        CropRect crop;
        try
        {
            crop = VideoCore.NormalizeCrop(CropValues(), _info.Width, _info.Height);
        }
        catch (VideoException)
        {
            return;
        }
        SetCropSpins(crop);
        if (updateOverlay)
            CropOverlay.SetCrop(crop);
        SyncOutputSize(fromWidth: _outputSizeDriver != "height");
        UpdateCropSummary();
    }

    private void CropSpinChanged()
    {
        if (!_cropSyncing)
            ApplyCrop();
    }

    private void OverlayCropChanged(CropRect crop)
    {
        if (_info is null || crop.Width < VideoCore.MinCropSize || crop.Height < VideoCore.MinCropSize)
            return;
        SetCropSpins(crop);
        ApplyCrop();
    }

    /// <summary>Makes the other side of the output size follow the rectangle's aspect ratio.</summary>
    private void SyncOutputSize(bool fromWidth)
    {
        if (_outputSyncing)
            return;
        var crop = CropValues();
        FrameSize size;
        try
        {
            size = fromWidth
                ? VideoCore.FitOutputSize(crop.Width, crop.Height, width: Value(OutputWidthSpin))
                : VideoCore.FitOutputSize(crop.Width, crop.Height, height: Value(OutputHeightSpin));
        }
        catch (VideoException)
        {
            return;
        }
        SetOutputSpins(size);
    }

    private void SetOutputSpins(FrameSize size)
    {
        _outputSyncing = true;
        try
        {
            SetValue(OutputWidthSpin, size.Width);
            SetValue(OutputHeightSpin, size.Height);
        }
        finally
        {
            _outputSyncing = false;
        }
    }

    private void OutputWidthChanged()
    {
        if (!_outputSyncing)
            _outputSizeDriver = "width";
        SyncOutputSize(fromWidth: true);
        UpdateCropSummary();
    }

    private void OutputHeightChanged()
    {
        if (!_outputSyncing)
            _outputSizeDriver = "height";
        SyncOutputSize(fromWidth: false);
        UpdateCropSummary();
    }

    /// <summary>Restores the saved crop bounds and output size for the current video.</summary>
    private void RestoreThumbnailSettings()
    {
        if (_info is null)
            return;
        // This also sets the spin boxes' ranges for the new source size.
        ResetCrop();
        var crop = CropValues();
        if (_settings.CropBounds is { } saved)
        {
            try
            {
                crop = VideoCore.NormalizeCrop(saved, _info.Width, _info.Height);
                SetCropSpins(crop);
                CropOverlay.SetCrop(crop);
            }
            catch (VideoException)
            {
                crop = CropValues();
            }
        }

        _outputSizeDriver = _settings.OutputSizeDriver == "height" ? "height" : "width";
        FrameSize size;
        try
        {
            size = _outputSizeDriver == "height"
                ? VideoCore.FitOutputSize(crop.Width, crop.Height, height: _settings.OutputSize.Height)
                : VideoCore.FitOutputSize(crop.Width, crop.Height, width: _settings.OutputSize.Width);
        }
        catch (VideoException)
        {
            size = VideoCore.FitOutputSize(crop.Width, crop.Height, width: 320);
        }
        SetOutputSpins(size);

        CropCheck.IsChecked = _settings.CropEnabled && CropCheck.IsEnabled;
        CropToggled();
    }

    private void UpdateCropSummary()
    {
        // The output size sets the smallest usable bitrate, so the estimate changes with it.
        TrimChanged();
        if (_info is null || CropCheck.IsChecked != true)
        {
            CropSummary.Text = "";
            return;
        }
        var crop = CropValues();
        var outputWidth = (int)Value(OutputWidthSpin);
        var outputHeight = (int)Value(OutputHeightSpin);
        var percent = outputWidth * 100.0 / Math.Max(1, crop.Width);
        var summary = string.Format(CultureInfo.CurrentCulture,
            "Keeping {0}×{1} at {2},{3} — encoded as {4}×{5} ({6:0}% of the selection)",
            crop.Width, crop.Height, crop.X, crop.Y, outputWidth, outputHeight, percent);
        if (outputWidth > crop.Width)
            summary += ". This enlarges the selection rather than shrinking it.";
        CropSummary.Text = summary;
    }

    /// <summary>The crop rectangle and output size for the encoder, or nulls when not cropping.</summary>
    private (CropRect? Crop, FrameSize? OutputSize) CropSettings()
    {
        if (CropCheck.IsChecked != true || _info is null)
            return (null, null);
        var crop = VideoCore.NormalizeCrop(CropValues(), _info.Width, _info.Height);
        var outputSize = VideoCore.FitOutputSize(crop.Width, crop.Height, width: Value(OutputWidthSpin));
        return (crop, outputSize);
    }
}
