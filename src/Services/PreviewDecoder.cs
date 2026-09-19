using System.Diagnostics;
using System.Globalization;
using videoclipper.Models;

namespace videoclipper.Services;

/// <summary>A decoded preview picture: BGRA pixels, their size, and the source time they show.</summary>
public sealed record PreviewFrame(byte[] Pixels, FrameSize Size, double Time, int Generation);

/// <summary>
/// Decodes preview pictures with ffmpeg, as raw BGRA frames read from its standard output. Stills
/// (seeking, frame stepping) run one ffmpeg per picture, and only the newest request waits, so
/// dragging across the timeline never queues up work. Playback runs one ffmpeg that streams frames,
/// shown at the video's frame rate. There is no audio.
/// </summary>
/// <remarks>
/// Frames are passed to the <c>present</c> callback on a worker thread, which must finish with the
/// pixels before its task completes (the buffer is reused). Each frame carries the generation it
/// was decoded for; <see cref="Play"/> and <see cref="Stop"/> start a new generation, so the UI
/// can ignore frames from playback that has been stopped (see <see cref="IsCurrent"/>).
/// </remarks>
public sealed class PreviewDecoder : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly string _path;
    private readonly VideoInfo _info;
    private readonly Func<PreviewFrame, Task> _present;
    private readonly object _lock = new();

    private StillRequest? _pendingStill;
    private bool _stillWorkerRunning;
    private Process? _stillProcess;
    private CancellationTokenSource? _playback;
    private volatile int _generation;
    private volatile bool _disposed;

    private sealed record StillRequest(double Time, bool Fast, FrameSize Size, int Generation);

    public PreviewDecoder(string path, VideoInfo info, Func<PreviewFrame, Task> present)
    {
        _path = path;
        _info = info;
        _present = present;
        Size = new FrameSize(Math.Max(2, info.Width), Math.Max(2, info.Height));
    }

    /// <summary>The size pictures are decoded at: the preview's size, never larger than the source.</summary>
    public FrameSize Size { get; set; }

    public bool IsPlaying => _playback is not null;

    /// <summary>Raised on a worker thread when playback reaches the end of the video.</summary>
    public event Action<int>? PlaybackEnded;

    /// <summary>Raised on a worker thread when ffmpeg fails, with its error message.</summary>
    public event Action<string>? Failed;

    /// <summary>True while frames from <paramref name="generation"/> should still be shown.</summary>
    public bool IsCurrent(int generation) => !_disposed && generation == _generation;

    /// <summary>
    /// The largest even size that fits the source into <paramref name="width"/> x <paramref name="height"/>
    /// device pixels, keeping its shape and never enlarging it.
    /// </summary>
    public static FrameSize FitSize(VideoInfo info, double width, double height)
    {
        if (info.Width < 2 || info.Height < 2)
            return new FrameSize(2, 2);
        var scale = Math.Min(1.0, Math.Min(width / info.Width, height / info.Height));
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;
        return new FrameSize(
            Math.Max(2, (int)(info.Width * scale) & ~1),
            Math.Max(2, (int)(info.Height * scale) & ~1));
    }

    /// <summary>
    /// Shows the frame at <paramref name="time"/>. A fast request shows the nearest earlier keyframe
    /// instead, which decodes much sooner; it is used while dragging across the timeline.
    /// </summary>
    public void RequestStill(double time, bool fast = false)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _pendingStill = new StillRequest(time, fast, Size, _generation);
            if (_stillWorkerRunning)
                return;
            _stillWorkerRunning = true;
        }
        _ = Task.Run(StillLoopAsync);
    }

    /// <summary>Plays from <paramref name="time"/> to the end of the video, until <see cref="Stop"/>.</summary>
    public void Play(double time)
    {
        Stop();
        var playback = new CancellationTokenSource();
        _playback = playback;
        var generation = Interlocked.Increment(ref _generation);
        var size = Size;
        _ = Task.Run(() => PlayLoopAsync(time, size, generation, playback.Token));
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        var playback = _playback;
        _playback = null;
        playback?.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        lock (_lock)
        {
            _pendingStill = null;
            if (_stillProcess is { } process)
                FFmpeg.Kill(process);
        }
    }

    private async Task StillLoopAsync()
    {
        while (true)
        {
            StillRequest request;
            lock (_lock)
            {
                if (_pendingStill is null || _disposed)
                {
                    _stillWorkerRunning = false;
                    return;
                }
                request = _pendingStill;
                _pendingStill = null;
            }

            try
            {
                var pixels = await DecodeStillAsync(request);
                if (pixels is not null && !_disposed)
                    await _present(new PreviewFrame(pixels, request.Size, request.Time, request.Generation));
            }
            catch (Exception ex) when (ex is VideoException or IOException or InvalidOperationException)
            {
                if (!_disposed)
                    Failed?.Invoke(ex.Message);
            }
        }
    }

    /// <summary>
    /// Decodes one frame. The seek goes half a frame early, so ffmpeg's "first frame at or after
    /// this time" lands on the frame nearest <paramref name="request"/>'s time despite rounding.
    /// </summary>
    private async Task<byte[]?> DecodeStillAsync(StillRequest request)
    {
        var halfFrame = 0.5 / _info.FrameRate;
        var seek = Math.Max(0.0, Math.Min(request.Time, _info.Duration) - halfFrame);
        var pixels = await ReadFramesAsync(request, seek, frameLimit: 1);
        if (pixels is null && seek > 0)
        {
            // Nothing at or after that time: the container's duration can run past the last video
            // frame. Decode the last second instead and keep its final frame.
            pixels = await ReadFramesAsync(request with { Fast = false }, Math.Max(0.0, seek - 1.0), frameLimit: null);
        }
        return pixels;
    }

    /// <summary>Runs ffmpeg from <paramref name="seek"/> and returns the last frame it wrote, if any.</summary>
    private async Task<byte[]?> ReadFramesAsync(StillRequest request, double seek, int? frameLimit)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (request.Fast)
            arguments.Add("-noaccurate_seek");
        arguments.AddRange(["-ss", Seconds(seek), "-i", _path]);
        arguments.AddRange(frameLimit is { } limit ? ["-frames:v", limit.ToString(Invariant)] : ["-t", "1.5"]);
        arguments.AddRange(OutputArguments(request.Size, frameRate: null));

        using var process = FFmpeg.Start("ffmpeg", arguments);
        lock (_lock)
            _stillProcess = process;
        try
        {
            var errors = process.StandardError.ReadToEndAsync();
            var frame = new byte[request.Size.Width * request.Size.Height * 4];
            var spare = new byte[frame.Length];
            byte[]? last = null;
            var stream = process.StandardOutput.BaseStream;
            while (await ReadFullyAsync(stream, spare, CancellationToken.None))
            {
                (frame, spare) = (spare, frame);
                last = frame;
            }
            await process.WaitForExitAsync();
            var error = (await errors).Trim();
            if (last is null && process.ExitCode != 0 && !_disposed)
                throw new VideoException(error.Length > 0 ? error : "ffmpeg could not decode this video.");
            return last;
        }
        finally
        {
            lock (_lock)
                _stillProcess = null;
        }
    }

    private async Task PlayLoopAsync(double time, FrameSize size, int generation, CancellationToken cancellation)
    {
        var frameRate = _info.FrameRate;
        // Times are counted in frames from the one nearest the start, as ffmpeg outputs a constant rate.
        var firstFrame = Math.Round(Math.Max(0.0, time) * frameRate);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", Seconds(Math.Max(0.0, firstFrame / frameRate - 0.5 / frameRate)),
            "-i", _path,
        };
        arguments.AddRange(OutputArguments(size, frameRate));

        Process process;
        try
        {
            process = FFmpeg.Start("ffmpeg", arguments);
        }
        catch (VideoException ex)
        {
            Failed?.Invoke(ex.Message);
            return;
        }

        using (process)
        using (cancellation.Register(() => FFmpeg.Kill(process)))
        {
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var pixels = new byte[size.Width * size.Height * 4];
            var stream = process.StandardOutput.BaseStream;
            var clock = new Stopwatch();
            try
            {
                for (var index = 0L; await ReadFullyAsync(stream, pixels, cancellation); index++)
                {
                    // The clock starts at the first frame, so ffmpeg's start-up time isn't counted.
                    if (index == 0)
                        clock.Start();
                    var due = TimeSpan.FromSeconds(index / frameRate);
                    var wait = due - clock.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, cancellation);
                    else if (-wait.TotalSeconds > 2.0 / frameRate)
                        continue; // Decoding can't keep up: drop this frame to stay on time.

                    var frameTime = Math.Min(_info.Duration, (firstFrame + index) / frameRate);
                    await _present(new PreviewFrame(pixels, size, frameTime, generation));
                }
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException) when (cancellation.IsCancellationRequested)
            {
                return;
            }

            var error = (await errors).Trim();
            if (process.ExitCode != 0 && error.Length > 0)
                Failed?.Invoke(error);
            if (!cancellation.IsCancellationRequested)
                PlaybackEnded?.Invoke(generation);
        }
    }

    /// <summary>The first video stream, scaled to <paramref name="size"/>, as raw BGRA frames on standard output.</summary>
    private static List<string> OutputArguments(FrameSize size, double? frameRate)
    {
        // The fps filter gives a constant rate, so frame n is n / rate seconds after the first.
        var filters = frameRate is { } rate ? $"fps={rate.ToString("0.######", Invariant)}," : "";
        var arguments = new List<string>
        {
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"{filters}scale={size.Width}:{size.Height}:flags=bilinear",
        };
        arguments.AddRange(["-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"]);
        return arguments;
    }

    /// <summary>Fills <paramref name="buffer"/>; false if the stream ended first.</summary>
    private static async Task<bool> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellation);
            if (read == 0)
                return false;
            filled += read;
        }
        return true;
    }

    private static string Seconds(double value) => value.ToString("0.000###", Invariant);
}
