using System.Text.Json;

namespace Cmux.Core.Config;

/// <summary>
/// Manages reading, writing, and caching of <see cref="CmuxSettings"/>.
/// Settings are stored at <c>%LOCALAPPDATA%/cmux/settings.json</c>.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static CmuxSettings? _current;

    /// <summary>
    /// The current in-memory settings instance (loaded on first access).
    /// </summary>
    public static CmuxSettings Current => _current ??= Load();

    /// <summary>
    /// Raised after <see cref="NotifyChanged"/> is called to signal that settings have been modified.
    /// </summary>
    public static event Action? SettingsChanged;

    /// <summary>
    /// Reads settings from disk. Returns a fresh default instance on any failure.
    /// </summary>
    public static CmuxSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new CmuxSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<CmuxSettings>(json, JsonOptions) ?? new CmuxSettings();
        }
        catch
        {
            return new CmuxSettings();
        }
    }

    /// <summary>
    /// Persists the given settings to disk atomically (write to .tmp, then move).
    /// </summary>
    public static void Save(CmuxSettings? settings = null)
    {
        settings ??= Current;

        try
        {
            Directory.CreateDirectory(SettingsDir);

            var tmpPath = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Swallow write failures (permission issues, disk full, etc.)
            // to avoid crashing the application.
        }
    }

    /// <summary>
    /// Resets settings to defaults and persists the result.
    /// </summary>
    public static CmuxSettings Reset()
    {
        _current = new CmuxSettings();
        Save(_current);
        return _current;
    }

    /// <summary>
    /// Raises the <see cref="SettingsChanged"/> event.
    /// Call after modifying <see cref="Current"/> properties.
    /// </summary>
    public static void NotifyChanged() => SettingsChanged?.Invoke();
}
