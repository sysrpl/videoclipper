using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using videoclipper.Helpers;
using videoclipper.Models;
using videoclipper.Services;

namespace videoclipper.Views;

// The preview: frames decoded by ffmpeg (no audio), playback, seeking and frame stepping.
public partial class MainWindow
{
    private PreviewDecoder? _decoder;
    /// <summary>The time of the frame being shown, or about to be.</summary>
    private double _position;
    /// <summary>Playback paused while the timeline is dragged, to carry on when it's released.</summary>
    private bool _resumeAfterSeek;
    // Two bitmaps, alternated, so the one on screen is never the one being written.
    private readonly WriteableBitmap?[] _bitmaps = new WriteableBitmap?[2];
    private int _bitmapIndex;
    private DispatcherTimer? _resizeTimer;

    private const double MinPreviewHeight = 120;

    private RowDefinition PreviewRow => ContentGrid.RowDefinitions[1];

    private void InitializePreview()
    {
        // The window scrolls when it's too short, so the preview can't just take the space left
        // over; it has its own height, set by dragging the handle below it.
        PreviewSplitter.DragDelta += (_, e) => SetPreviewHeight(PreviewRow.Height.Value + e.Vector.Y);
        if (_settings.PreviewHeight > 0)
            SetPreviewHeight(_settings.PreviewHeight);
        else
            Opened += (_, _) => Dispatcher.UIThread.Post(FillWindowWithPreview, DispatcherPriority.Loaded);

        Timeline.SeekRequested += (seconds, final) => SeekTo(seconds, final);
        PreviewArea.PointerPressed += (_, _) => PreviewArea.Focus();
        PreviewArea.KeyDown += Preview_KeyDown;

        // Decode at the preview's size, so a resize waits until the dragging stops.
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            UpdateDecodeSize();
        };
        PreviewArea.SizeChanged += (_, _) =>
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
    }

    private void SetPreviewHeight(double height) =>
        PreviewRow.Height = new GridLength(Math.Round(Math.Max(MinPreviewHeight, height)));

    /// <summary>First start: makes the preview take the space the other controls leave in the window.</summary>
    private void FillWindowWithPreview()
    {
        var others = ContentGrid.DesiredSize.Height - PreviewRow.ActualHeight;
        SetPreviewHeight(PageScroller.Viewport.Height - others);
    }

    /// <summary>Starts previewing a newly opened video at its first frame.</summary>
    private void OpenPreview(string path, VideoInfo info)
    {
        StopPlayback();
        _decoder?.Dispose();
        var decoder = new PreviewDecoder(path, info, PresentFrameAsync);
        decoder.PlaybackEnded += generation => Dispatcher.UIThread.Post(() =>
        {
            if (decoder.IsCurrent(generation))
                StopPlayback();
        });
        decoder.Failed += message => Dispatcher.UIThread.Post(() =>
        {
            AppendLog($"Preview error: {message}\n");
            if (PreviewImage.Source is null)
            {
                PreviewMessage.Text = $"The preview could not be shown.\n{message}";
                PreviewMessage.IsVisible = true;
            }
        });
        _decoder = decoder;

        PreviewImage.Source = null;
        PreviewMessage.Text = "Loading the preview…";
        PreviewMessage.IsVisible = true;
        decoder.Size = PreviewDecodeSize(info);
        _position = 0;
        UpdatePositionDisplay();
        decoder.RequestStill(0);
    }

    /// <summary>The preview's size in device pixels, for the video to fit into.</summary>
    private FrameSize PreviewDecodeSize(VideoInfo info)
    {
        var border = PreviewArea.BorderThickness;
        var width = PreviewArea.Bounds.Width - border.Left - border.Right;
        var height = PreviewArea.Bounds.Height - border.Top - border.Bottom;
        return PreviewDecoder.FitSize(info, width * RenderScaling, height * RenderScaling);
    }

    private void UpdateDecodeSize()
    {
        if (_decoder is not { } decoder || _info is null)
            return;
        var size = PreviewDecodeSize(_info);
        if (size == decoder.Size)
            return;
        decoder.Size = size;
        if (decoder.IsPlaying)
            decoder.Play(_position);
        else
            decoder.RequestStill(_position);
    }

    /// <summary>Called by the decoder on a worker thread; copies the frame into a bitmap on the UI thread.</summary>
    private Task PresentFrameAsync(PreviewFrame frame) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowFrame(frame), DispatcherPriority.Render).GetTask();

    private void ShowFrame(PreviewFrame frame)
    {
        if (_decoder is not { } decoder || !decoder.IsCurrent(frame.Generation))
            return;

        var index = _bitmapIndex ^ 1;
        var pixelSize = new PixelSize(frame.Size.Width, frame.Size.Height);
        var bitmap = _bitmaps[index];
        if (bitmap is null || bitmap.PixelSize != pixelSize)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(pixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _bitmaps[index] = bitmap;
        }
        using (var buffer = bitmap.Lock())
        {
            var rowBytes = frame.Size.Width * 4;
            for (var row = 0; row < frame.Size.Height; row++)
                Marshal.Copy(frame.Pixels, row * rowBytes, buffer.Address + row * buffer.RowBytes, rowBytes);
        }
        _bitmapIndex = index;
        PreviewImage.Source = bitmap;
        PreviewMessage.IsVisible = false;

        if (!decoder.IsPlaying)
            return;
        _position = frame.Time;
        UpdatePositionDisplay();
        var end = Value(EndSpin);
        if (_position >= end)
        {
            StopPlayback();
            SeekTo(end);
        }
    }

    private void UpdatePositionDisplay()
    {
        Timeline.SetPosition(_position);
        TimeText.Text = $"{VideoCore.FormatTime(_position)} / {VideoCore.FormatTime(_info?.Duration ?? 0)}";
    }

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (_decoder is not { } decoder || _info is null)
            return;
        if (decoder.IsPlaying || _resumeAfterSeek)
        {
            StopPlayback();
            return;
        }
        if (_position >= Value(EndSpin) - 0.05)
            SeekTo(Value(StartSpin));
        decoder.Play(_position);
        SetPlayButton(playing: true);
    }

    private void StopPlayback()
    {
        _resumeAfterSeek = false;
        _decoder?.Stop();
        SetPlayButton(playing: false);
    }

    private void SetPlayButton(bool playing)
    {
        PlayIcon.Text = playing ? Icons.Pause : Icons.Play;
        PlayLabel.Text = playing ? "Pause" : "Play";
    }

    /// <summary>
    /// Shows the frame at <paramref name="seconds"/>. While the timeline is still being dragged
    /// (<paramref name="final"/> false) keyframes are shown, as they decode far sooner. Playback
    /// pauses during the drag and carries on from where it is released.
    /// </summary>
    private void SeekTo(double seconds, bool final = true)
    {
        if (_decoder is not { } decoder || _info is null)
            return;
        seconds = Math.Clamp(seconds, 0.0, _info.Duration);
        _position = seconds;
        UpdatePositionDisplay();

        if (decoder.IsPlaying || _resumeAfterSeek)
        {
            if (!final)
            {
                decoder.Stop();
                _resumeAfterSeek = true;
                decoder.RequestStill(seconds, fast: true);
                return;
            }
            _resumeAfterSeek = false;
            var end = Value(EndSpin);
            if (seconds < end)
            {
                decoder.Play(seconds);
                return;
            }
            // Past the end point: stop there, as playback would have.
            StopPlayback();
            _position = seconds = end;
            UpdatePositionDisplay();
        }
        decoder.RequestStill(seconds, fast: !final);
    }

    private void SetStartHere_Click(object? sender, RoutedEventArgs e) => SetValue(StartSpin, _position);

    private void SetEndHere_Click(object? sender, RoutedEventArgs e) => SetValue(EndSpin, _position);

    private void JumpToStart_Click(object? sender, RoutedEventArgs e) => SeekTo(Value(StartSpin));

    private void JumpToEnd_Click(object? sender, RoutedEventArgs e) => SeekTo(Value(EndSpin));

    /// <summary>Left and Right move one source frame; holding the key repeats.</summary>
    private void Preview_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || e.KeyModifiers != KeyModifiers.None)
            return;
        e.Handled = true;
        if (_info is null)
            return;
        StopPlayback();
        var frameRate = _info.FrameRate;
        var frame = Math.Round(_position * frameRate) + (e.Key == Key.Left ? -1 : 1);
        SeekTo(frame / frameRate);
    }
}
