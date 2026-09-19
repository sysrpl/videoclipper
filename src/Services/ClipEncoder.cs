using System.Globalization;
using videoclipper.Models;

namespace videoclipper.Services;

/// <summary>
/// Runs an encode: two ffmpeg passes for MP4, one for OGV. The callbacks are called on worker
/// threads, so a window must pass them on to its UI thread.
/// </summary>
public sealed class ClipEncoder
{
    /// <summary>A short status for the progress bar, e.g. "Encoding pass 1 of 2…".</summary>
    public Action<string>? Status { get; init; }

    /// <summary>How much of the whole encode is done, from 0 to 1.</summary>
    public Action<double>? Progress { get; init; }

    /// <summary>A line (or lines) for the encoding details log, ending in a newline.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Encodes the clip and returns the finished message.</summary>
    /// <exception cref="VideoException">ffmpeg failed, or the result is over the size limit.</exception>
    /// <exception cref="OperationCanceledException">The encode was cancelled.</exception>
    public async Task<string> EncodeAsync(EncodeOptions options, CancellationToken cancellation)
    {
        var duration = options.End - options.Start;
        var tempFolder = Directory.CreateTempSubdirectory("videoclipper-");
        try
        {
            var plan = VideoCore.BuildEncodeCommands(options, Path.Combine(tempFolder.FullName, "ffmpeg2pass"));
            // OGV has no first pass: constant-quality Theora needs only one.
            var totalPasses = plan.FirstPass is null ? 1 : 2;
            if (plan.FirstPass is { } first)
            {
                Log?.Invoke($"Using {plan.VideoKbps} kbps video bitrate\n");
                await RunPassAsync(first, 1, duration, totalPasses, cancellation);
            }
            else
            {
                Log?.Invoke(VideoCore.DescribeTheoraQuality(options.TheoraQuality) + "\n");
            }
            await RunPassAsync(plan.SecondPass, totalPasses, duration, totalPasses, cancellation);
        }
        finally
        {
            try { tempFolder.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var actualMb = new FileInfo(options.OutputPath).Length / 1_000_000.0;
        // A Theora clip was never encoded against a size limit, so there is nothing to hold it to.
        if (options.VideoFormat != VideoCore.Ogv && actualMb >= options.TargetMb)
            throw new VideoException(string.Format(CultureInfo.CurrentCulture,
                "Encoding finished, but the file is {0:0.0} MB (over the {1:0.0} MB limit).", actualMb, options.TargetMb));
        return string.Format(CultureInfo.CurrentCulture, "Finished: {0:0.0} MB\n{1}", actualMb, options.OutputPath);
    }

    private async Task RunPassAsync(IReadOnlyList<string> arguments, int passNumber, double duration,
        int totalPasses, CancellationToken cancellation)
    {
        var label = totalPasses > 1 ? $"Encoding pass {passNumber} of {totalPasses}…" : "Encoding…";
        Status?.Invoke(label);
        Log?.Invoke(label + "\n");

        using var process = FFmpeg.Start("ffmpeg", arguments);
        process.StandardInput.Close();
        using var kill = cancellation.Register(() => FFmpeg.Kill(process));

        // Progress arrives on standard output (-progress pipe:1) and errors on standard error.
        var outputLines = new Queue<string>();
        void Remember(string line)
        {
            lock (outputLines)
            {
                outputLines.Enqueue(line);
                if (outputLines.Count > 20)
                    outputLines.Dequeue();
            }
        }

        var errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length > 0)
                    Remember(line.Trim());
            }
        });

        while (await process.StandardOutput.ReadLineAsync() is { } raw)
        {
            var line = raw.Trim();
            if (!line.StartsWith("out_time_us=") && !line.StartsWith("out_time_ms="))
                continue;
            // Both keys hold microseconds (out_time_ms is misnamed in ffmpeg).
            if (long.TryParse(line.AsSpan(line.IndexOf('=') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsed))
            {
                var withinPass = Math.Clamp(elapsed / 1_000_000.0 / duration, 0.0, 1.0);
                Progress?.Invoke((passNumber - 1 + withinPass) / totalPasses);
            }
        }
        await errors;
        await process.WaitForExitAsync(CancellationToken.None);
        cancellation.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            string detail;
            lock (outputLines)
                detail = string.Join("\n", outputLines);
            Log?.Invoke(detail + "\n");
            var summary = totalPasses > 1 ? $"FFmpeg pass {passNumber} failed." : "FFmpeg failed.";
            throw new VideoException($"{summary}\n{detail}");
        }
    }
}
