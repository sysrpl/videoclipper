using System.ComponentModel;
using System.Diagnostics;
using videoclipper.Models;

namespace videoclipper.Services;

/// <summary>Starts the ffmpeg and ffprobe programs, which must be on the PATH.</summary>
public static class FFmpeg
{
    /// <summary>How to install FFmpeg on this operating system.</summary>
    public static string InstallHint =>
        OperatingSystem.IsWindows() ? "Install it with: winget install Gyan.FFmpeg" :
        OperatingSystem.IsMacOS() ? "Install it with: brew install ffmpeg" :
        "Install it with: sudo apt install ffmpeg";

    /// <summary>The null output device, for the first pass of a two-pass encode.</summary>
    public static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>True if <paramref name="tool"/> (ffmpeg or ffprobe) is in a folder on the PATH.</summary>
    public static bool IsInstalled(string tool)
    {
        var fileName = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return folders.Any(folder => folder.Length > 0 && File.Exists(Path.Combine(folder, fileName)));
    }

    /// <summary>
    /// Starts <paramref name="tool"/> with its output redirected. Standard input is redirected too,
    /// so ffmpeg never waits for a keypress.
    /// </summary>
    public static Process Start(string tool, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        try
        {
            return Process.Start(info) ?? throw new VideoException($"{tool} could not be started.");
        }
        catch (Win32Exception)
        {
            throw new VideoException($"{tool} was not found. {InstallHint}");
        }
    }

    /// <summary>Runs <paramref name="tool"/> to completion and returns its exit code and output.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string tool, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        using var process = Start(tool, arguments);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static readonly Lazy<Task<string?>> CachedVersion = new(() => Task.Run(async () =>
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await RunAsync("ffmpeg", ["-version"], timeout.Token);
            // "ffmpeg version 4.2.7-0ubuntu0.1 Copyright (c) ...": keep just the version.
            var words = result.Output.Split('\n', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length >= 3 && words[1] == "version" ? words[2] : string.Join(' ', words);
        }
        catch (Exception)
        {
            return (string?)null;
        }
    }));

    /// <summary>
    /// The version from "ffmpeg -version", e.g. "4.2.7-0ubuntu0.1", or null if ffmpeg can't be run. Runs on a worker
    /// thread (never block the UI thread on it) and is only worked out once.
    /// </summary>
    public static Task<string?> VersionAsync() => CachedVersion.Value;

    /// <summary>Stops a process that may already have exited.</summary>
    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // It exited on its own in the meantime.
        }
    }
}
