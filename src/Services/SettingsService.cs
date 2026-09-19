using System.Text.Json;
using videoclipper.Models;

namespace videoclipper.Services;

/// <summary>Values the main window remembers between sessions.</summary>
public sealed class AppSettings
{
    public double MaximumSizeMb { get; set; } = 75;
    public int TheoraQuality { get; set; } = VideoCore.DefaultTheoraQuality;
    public string VideoFormat { get; set; } = VideoCore.Mp4;
    /// <summary>0 means no audio.</summary>
    public int AudioKbps { get; set; } = 128;

    /// <summary>The preview's height, as set with its resize handle; 0 fills the window on first start.</summary>
    public double PreviewHeight { get; set; }

    // Thumbnail (Cropping tab) values, restored for the next video and clamped to its size.
    public bool CropEnabled { get; set; }
    public CropRect? CropBounds { get; set; }
    public FrameSize OutputSize { get; set; } = new(320, 180);
    /// <summary>"width" or "height": the side of the output size the user last typed.</summary>
    public string OutputSizeDriver { get; set; } = "width";
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as settings.json in the app data folder
/// (~/.config/videoclipper on Linux).
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsService(string? folder = null)
    {
        folder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "videoclipper");
        _path = Path.Combine(folder, "settings.json");
        Settings = Load();
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Saves through a temporary file, so a crash can't leave a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        Settings = settings;
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saving settings must never stop the window from closing.
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }

    /// <summary>The saved settings, or the defaults if there are none or the file can't be read.</summary>
    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall back to the defaults; the next save writes a good file again.
        }
        return new AppSettings();
    }
}
