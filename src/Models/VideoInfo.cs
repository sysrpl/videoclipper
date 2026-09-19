namespace videoclipper.Models;

/// <summary>What ffprobe reports about a video file.</summary>
public sealed record VideoInfo(
    double Duration,
    long Size,
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio);

/// <summary>A rectangle in source pixels: left, top, width and height.</summary>
public readonly record struct CropRect(int X, int Y, int Width, int Height);

/// <summary>An output size in pixels.</summary>
public readonly record struct FrameSize(int Width, int Height);

/// <summary>A problem that can be shown directly to the user.</summary>
public sealed class VideoException(string message) : Exception(message);
