using System.Globalization;
using System.Text.Json;
using videoclipper.Models;

namespace videoclipper.Services;

/// <summary>The settings for one encode, as chosen in the main window.</summary>
public sealed record EncodeOptions(
    string InputPath,
    string OutputPath,
    double Start,
    double End,
    double TargetMb,
    int AudioKbps,
    int MaxWidth,
    bool HasAudio,
    bool FadeAudio,
    bool FadeVideo,
    CropRect? Crop,
    FrameSize? OutputSize,
    string VideoFormat,
    int TheoraQuality);

/// <summary>
/// The ffmpeg argument lists for an encode. MP4 output targets a size, so it has two passes and a
/// video bitrate. OGV output encodes at a constant Theora quality, so FirstPass and VideoKbps are
/// null and only one pass runs.
/// </summary>
public sealed record EncodePlan(IReadOnlyList<string>? FirstPass, IReadOnlyList<string> SecondPass, int? VideoKbps);

/// <summary>Probing, bitrate, cropping and ffmpeg command helpers.</summary>
public static class VideoCore
{
    public const double SizeHeadroom = 0.95;
    // Theora encodes at a constant quality instead of a target bitrate, so an OGV clip has no
    // predictable size and needs only one pass.
    public const string Mp4 = "mp4";
    public const string Ogv = "ogv";
    public static readonly IReadOnlyList<string> VideoFormats = [Mp4, Ogv];
    public const int MinTheoraQuality = 1;
    public const int MaxTheoraQuality = 9;
    public const int DefaultTheoraQuality = 7;
    public const int MinCropSize = 16;
    public const int MinVideoKbps = 150;
    public const int MinThumbnailVideoKbps = 25;
    public const int ThumbnailReferencePixels = 640 * 360;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string FormatTime(double seconds)
    {
        seconds = double.IsFinite(seconds) ? Math.Max(0.0, seconds) : 0.0;
        var totalMinutes = Math.Floor(seconds / 60.0);
        seconds -= totalMinutes * 60.0;
        var hours = (int)(totalMinutes / 60);
        var minutes = (int)(totalMinutes % 60);
        var secondsText = seconds.ToString("00.00", Invariant);
        return hours > 0
            ? $"{hours}:{minutes:00}:{secondsText}"
            : $"{minutes:00}:{secondsText}";
    }

    /// <summary>Converts an ffprobe frame-rate fraction such as 60000/1001 to a number.</summary>
    public static double ParseFrameRate(string? value)
    {
        var parts = (value ?? "").Split('/', 2);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, Invariant, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, Invariant, out var denominator)
            && denominator != 0)
        {
            var rate = numerator / denominator;
            if (rate is > 0 and < 1000)
                return rate;
        }
        return 30.0;
    }

    /// <summary>Returns basic media information from ffprobe.</summary>
    public static async Task<VideoInfo> ProbeVideoAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await FFmpeg.RunAsync("ffprobe",
        [
            "-v", "error",
            "-show_entries", "format=duration,size:stream=index,codec_type,width,height,avg_frame_rate",
            "-of", "json",
            path,
        ], cancellationToken);

        if (result.ExitCode != 0)
        {
            var detail = result.Error.Trim();
            throw new VideoException(detail.Length > 0 ? detail : "ffprobe could not read this file.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            var format = root.GetProperty("format");
            var duration = double.Parse(format.GetProperty("duration").GetString()!, Invariant);

            var streams = root.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList()
                : [];
            var video = streams.FirstOrDefault(s => CodecType(s) == "video");
            if (video.ValueKind == JsonValueKind.Undefined)
                throw new VideoException("The selected file does not contain a video stream.");

            return new VideoInfo(
                Duration: duration,
                Size: format.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out var bytes) ? bytes : 0,
                Width: IntProperty(video, "width"),
                Height: IntProperty(video, "height"),
                FrameRate: ParseFrameRate(video.TryGetProperty("avg_frame_rate", out var rate) ? rate.GetString() : "30/1"),
                HasAudio: streams.Any(s => CodecType(s) == "audio"));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or JsonException
                                       or InvalidOperationException or ArgumentNullException)
        {
            throw new VideoException("The video's duration could not be determined.");
        }

        static string? CodecType(JsonElement stream) =>
            stream.TryGetProperty("codec_type", out var type) ? type.GetString() : null;

        static int IntProperty(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    }

    /// <summary>Returns a practical bitrate floor, reduced for small explicit outputs.</summary>
    public static int MinimumVideoBitrateKbps(FrameSize? outputSize = null)
    {
        if (outputSize is not { } size)
            return MinVideoKbps;
        if (size.Width < 2 || size.Height < 2)
            throw new VideoException("The output size is not valid.");
        var scaledFloor = (int)Math.Ceiling(
            MinVideoKbps * (double)size.Width * size.Height / ThumbnailReferencePixels);
        return Math.Max(MinThumbnailVideoKbps, Math.Min(MinVideoKbps, scaledFloor));
    }

    /// <summary>Calculates a conservative, resolution-aware video bitrate.</summary>
    public static int CalculateVideoBitrateKbps(
        double duration, double targetMb, int audioKbps, bool hasAudio = true, FrameSize? outputSize = null)
    {
        audioKbps = hasAudio ? audioKbps : 0;
        if (duration <= 0)
            throw new VideoException("The selected clip has no duration.");
        if (targetMb <= 0)
            throw new VideoException("The size limit must be greater than zero.");

        var usableBits = targetMb * 1_000_000 * 8 * SizeHeadroom;
        var totalKbps = usableBits / duration / 1000.0;
        var videoKbps = (int)Math.Floor(totalKbps - audioKbps);
        var minimumKbps = MinimumVideoBitrateKbps(outputSize);
        if (videoKbps < minimumKbps)
            throw new VideoException(
                $"That size limit leaves only {Math.Max(0, videoKbps)} kbps for video; this output needs " +
                $"at least {minimumKbps} kbps. Shorten the clip, reduce the audio bitrate, or " +
                "increase the size limit.");
        return videoKbps;
    }

    /// <summary>
    /// The size the encode produces without cropping: the source, shrunk to
    /// <paramref name="maxWidth"/> if wider (0 keeps the original), as scale='min(iw,W)':-2 does.
    /// </summary>
    public static FrameSize ScaledOutputSize(int sourceWidth, int sourceHeight, int maxWidth)
    {
        if (maxWidth <= 0 || sourceWidth <= maxWidth)
            return new FrameSize(sourceWidth, sourceHeight);
        return new FrameSize(maxWidth, MakeEven((double)sourceHeight * maxWidth / sourceWidth));
    }

    /// <summary>
    /// A video bitrate that gives good quality with two-pass H.264 (preset slow): 0.08 bits per
    /// pixel per frame at 30 fps, about 5 Mbps for 1080p30. Higher frame rates need more, but not
    /// in proportion, as neighbouring frames are more alike: 1080p60 gets about 8.4 Mbps.
    /// </summary>
    public static int GoodQualityVideoKbps(FrameSize outputSize, double frameRate)
    {
        const double bitsPerPixel = 0.08;
        const double referenceFrameRate = 30.0;
        frameRate = frameRate > 0 ? frameRate : referenceFrameRate;
        var pixelsPerSecond = (double)outputSize.Width * outputSize.Height * referenceFrameRate;
        var kbps = pixelsPerSecond * bitsPerPixel * Math.Pow(frameRate / referenceFrameRate, 0.75) / 1000.0;
        return Math.Max(MinimumVideoBitrateKbps(outputSize), (int)Math.Ceiling(kbps));
    }

    /// <summary>
    /// The maximum size, in MB rounded up to 0.1, that leaves room for
    /// <see cref="GoodQualityVideoKbps"/> once <see cref="CalculateVideoBitrateKbps"/> takes its safety margin.
    /// </summary>
    public static double SuggestedSizeMb(double duration, FrameSize outputSize, double frameRate,
        int audioKbps, bool hasAudio)
    {
        if (duration <= 0)
            throw new VideoException("The end time must be later than the start time.");
        var totalKbps = GoodQualityVideoKbps(outputSize, frameRate) + (hasAudio ? audioKbps : 0);
        var megabytes = totalKbps * 1000.0 * duration / 8 / 1_000_000 / SizeHeadroom;
        return Math.Max(0.1, Math.Ceiling(megabytes * 10) / 10);
    }

    public static double EstimatedSizeMb(double duration, int videoKbps, int audioKbps, bool hasAudio = true)
    {
        var audio = hasAudio ? audioKbps : 0;
        return duration * (videoKbps + audio) * 1000 / 8 / 1_000_000;
    }

    /// <summary>Rounds to the nearest even number, which yuv420p output requires.</summary>
    public static int MakeEven(double value, int minimum = 2) =>
        Math.Max(minimum, (int)Math.Floor(value / 2.0 + 0.5) * 2);

    /// <summary>Rounds down to an even number so a rectangle never grows past its edge.</summary>
    public static int FloorEven(double value, int minimum = 0)
    {
        var floor = (int)Math.Floor(value);
        return Math.Max(minimum, floor - floor % 2);
    }

    /// <summary>Clamps a crop rectangle to the frame and rounds it to even pixels.</summary>
    public static CropRect NormalizeCrop(double x, double y, double width, double height, int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
            throw new VideoException("The crop rectangle is not valid.");
        if (sourceWidth < 2 || sourceHeight < 2)
            throw new VideoException("The video's dimensions are unknown, so it cannot be cropped.");

        sourceWidth -= sourceWidth % 2;
        sourceHeight -= sourceHeight % 2;
        var left = FloorEven(Math.Max(0.0, Math.Min(sourceWidth - 2.0, x)));
        var top = FloorEven(Math.Max(0.0, Math.Min(sourceHeight - 2.0, y)));
        var cropWidth = FloorEven(Math.Max(2.0, Math.Min(sourceWidth - left, width)), 2);
        var cropHeight = FloorEven(Math.Max(2.0, Math.Min(sourceHeight - top, height)), 2);
        return new CropRect(left, top, cropWidth, cropHeight);
    }

    public static CropRect NormalizeCrop(CropRect crop, int sourceWidth, int sourceHeight) =>
        NormalizeCrop(crop.X, crop.Y, crop.Width, crop.Height, sourceWidth, sourceHeight);

    /// <summary>Returns an even output size with the same aspect ratio as the crop.</summary>
    public static FrameSize FitOutputSize(double cropWidth, double cropHeight, double? width = null, double? height = null)
    {
        if (cropWidth < 2 || cropHeight < 2)
            throw new VideoException("The crop rectangle is too small.");
        if (width is > 0)
        {
            var outWidth = MakeEven(width.Value);
            return new FrameSize(outWidth, MakeEven(outWidth * cropHeight / cropWidth));
        }
        if (height is > 0)
        {
            var outHeight = MakeEven(height.Value);
            return new FrameSize(MakeEven(outHeight * cropWidth / cropHeight), outHeight);
        }
        return new FrameSize(MakeEven(cropWidth), MakeEven(cropHeight));
    }

    /// <summary>Validates a crop rectangle that <see cref="NormalizeCrop(CropRect,int,int)"/> has already rounded.</summary>
    private static CropRect? CheckedCrop(CropRect? crop)
    {
        if (crop is not { } c)
            return null;
        if (c.X < 0 || c.Y < 0 || c.Width < 2 || c.Height < 2)
            throw new VideoException("The crop rectangle is not valid.");
        if (c.X % 2 != 0 || c.Y % 2 != 0 || c.Width % 2 != 0 || c.Height % 2 != 0)
            throw new VideoException("The crop rectangle must use even pixel values.");
        return c;
    }

    /// <summary>Validates an explicit output size in pixels.</summary>
    private static FrameSize? CheckedOutputSize(FrameSize? outputSize)
    {
        if (outputSize is not { } size)
            return null;
        if (size.Width < 2 || size.Height < 2)
            throw new VideoException("The output size must be at least 2 pixels on each side.");
        if (size.Width % 2 != 0 || size.Height % 2 != 0)
            throw new VideoException("The output size must use even pixel values.");
        return size;
    }

    /// <summary>Validates the container/codec choice.</summary>
    private static string CheckedVideoFormat(string? videoFormat)
    {
        var format = (videoFormat ?? Mp4).ToLowerInvariant();
        if (!VideoFormats.Contains(format))
            throw new VideoException($"Unknown output format: {format}");
        return format;
    }

    /// <summary>Validates the libtheora -q:v value.</summary>
    private static int CheckedTheoraQuality(int quality)
    {
        if (quality is < MinTheoraQuality or > MaxTheoraQuality)
            throw new VideoException(
                $"The Theora quality must be between {MinTheoraQuality} and {MaxTheoraQuality}.");
        return quality;
    }

    /// <summary>Describes a quality level, since OGV output has no predictable size.</summary>
    public static string DescribeTheoraQuality(int quality)
    {
        quality = CheckedTheoraQuality(quality);
        var detail = quality <= 3 ? "small file, visible artefacts"
            : quality <= 6 ? "balanced"
            : "large file, best detail";
        return $"Theora quality {quality} of {MaxTheoraQuality} • {detail}";
    }

    /// <summary>Returns the filename extension that matches a format.</summary>
    public static string OutputExtension(string videoFormat) => "." + CheckedVideoFormat(videoFormat);

    private static string Seconds(double value) => value.ToString("0.000", Invariant);

    private static List<string> CommonInputArgs(string inputPath, double start, double duration) =>
    [
        // With transcoding, input-side -ss seeks to the prior keyframe and then decodes and
        // discards up to the requested instant. It is accurate and ensures filters see the
        // selected clip starting at timestamp zero.
        "-hide_banner", "-y", "-loglevel", "error",
        "-ss", Seconds(start),
        "-i", inputPath,
        "-t", Seconds(duration),
    ];

    /// <summary>Builds the ffmpeg argument lists and returns them with the chosen bitrate.</summary>
    /// <param name="passLogPath">Where the first pass writes its statistics for the second.</param>
    public static EncodePlan BuildEncodeCommands(EncodeOptions options, string passLogPath)
    {
        var videoFormat = CheckedVideoFormat(options.VideoFormat);
        var duration = options.End - options.Start;
        if (options.Start < 0 || duration <= 0)
            throw new VideoException("The end time must be later than the start time.");
        if ((options.FadeAudio || options.FadeVideo) && duration < 1.0)
            throw new VideoException("A clip must be at least 1 second long to use 0.5-second fades.");
        var crop = CheckedCrop(options.Crop);
        var outputSize = CheckedOutputSize(options.OutputSize);

        int? videoKbps;
        List<string> videoArgs;
        if (videoFormat == Ogv)
        {
            videoKbps = null;
            videoArgs =
            [
                "-map", "0:v:0",
                "-c:v", "libtheora",
                "-q:v", CheckedTheoraQuality(options.TheoraQuality).ToString(Invariant),
                // Godot's Theora decoder only handles 4:2:0 chroma.
                "-pix_fmt", "yuv420p",
            ];
        }
        else
        {
            videoKbps = CalculateVideoBitrateKbps(
                duration, options.TargetMb, options.AudioKbps, options.HasAudio, outputSize);
            videoArgs =
            [
                "-map", "0:v:0",
                "-c:v", "libx264",
                "-b:v", $"{videoKbps}k",
                "-preset", "slow",
                "-pix_fmt", "yuv420p",
                "-profile:v", "high",
            ];
        }

        var videoFilters = new List<string>();
        // Output seeking can leave filters seeing the source timestamps even though the muxer
        // later rebases them. Rebase both streams so fades are relative to the selected clip and
        // audio and video remain aligned.
        if (options.FadeAudio || options.FadeVideo)
            videoFilters.Add("setpts=PTS-STARTPTS");
        if (crop is { } c)
        {
            // crop takes width:height:x:y, unlike the left/top-first order used by the user interface.
            videoFilters.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}");
        }
        if (outputSize is { } size)
        {
            // An explicit crop size replaces the maximum-width rule, and setsar keeps browsers
            // from stretching the thumbnail to the source's ratio.
            videoFilters.Add($"scale={size.Width}:{size.Height}");
            videoFilters.Add("setsar=1");
        }
        else if (options.MaxWidth > 0)
        {
            // Never upscale. -2 selects an even height, which yuv420p requires.
            videoFilters.Add($"scale='min(iw,{options.MaxWidth})':-2");
        }
        if (options.FadeVideo)
        {
            videoFilters.Add("fade=t=in:st=0:d=0.5");
            videoFilters.Add($"fade=t=out:st={Seconds(duration - 0.5)}:d=0.5");
        }
        if (videoFilters.Count > 0)
            videoArgs.AddRange(["-vf", string.Join(",", videoFilters)]);

        List<string>? first = null;
        if (videoFormat == Mp4)
        {
            first = CommonInputArgs(options.InputPath, options.Start, duration);
            first.AddRange(videoArgs);
            first.AddRange(
            [
                "-pass", "1",
                "-passlogfile", passLogPath,
                "-an", "-sn", "-dn",
                "-progress", "pipe:1", "-nostats",
                "-f", "mp4", FFmpeg.NullDevice,
            ]);
        }

        var second = CommonInputArgs(options.InputPath, options.Start, duration);
        second.AddRange(videoArgs);
        if (videoFormat == Mp4)
            second.AddRange(["-pass", "2", "-passlogfile", passLogPath]);
        if (options.HasAudio)
        {
            second.AddRange(
            [
                "-map", "0:a:0?",
                // Ogg carries Vorbis, Opus, Speex and FLAC, but Godot's player accepts Vorbis only.
                "-c:a", videoFormat == Ogv ? "libvorbis" : "aac",
                "-b:a", $"{options.AudioKbps}k",
            ]);
            if (options.FadeAudio)
                second.AddRange(["-af", $"asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.5,afade=t=out:st={Seconds(duration - 0.5)}:d=0.5"]);
            else if (options.FadeVideo)
                second.AddRange(["-af", "asetpts=PTS-STARTPTS"]);
        }
        else
        {
            second.Add("-an");
        }
        second.AddRange(["-sn", "-dn"]);
        if (videoFormat == Ogv)
            second.AddRange(["-f", "ogg"]);
        else
            second.AddRange(["-movflags", "+faststart"]);
        second.AddRange(
        [
            "-max_muxing_queue_size", "2048",
            "-progress", "pipe:1", "-nostats",
            options.OutputPath,
        ]);
        return new EncodePlan(first, second, videoKbps);
    }
}
