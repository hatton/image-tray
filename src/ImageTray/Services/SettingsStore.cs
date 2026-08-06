using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageTray.Models;

namespace ImageTray.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Writes go to a temp file first and
/// are then swapped into place, so a crash part-way through a save cannot leave
/// behind a half-written settings file.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly object _writeLock = new();

    public SettingsStore()
        : this(DefaultPath(), LegacyPath())
    {
    }

    public SettingsStore(string path)
        : this(path, legacyPath: null)
    {
    }

    internal SettingsStore(string path, string? legacyPath)
    {
        _path = path;
        _legacyPath = legacyPath;
    }

    public string Path => _path;

    public static string DefaultPath() => AppDataPath("ImageTray");

    /// <summary>
    /// Where the settings lived when the app was called Screenshot Tray. Read once,
    /// when there is nothing at the current location, so the rename does not cost you
    /// your watched folder and window position. The first save writes to the new
    /// place and the old folder is then left alone.
    /// </summary>
    internal static string LegacyPath() => AppDataPath("ScreenshotTray");

    private static string AppDataPath(string folderName) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        folderName,
        "settings.json");

    /// <summary>
    /// Reads the settings file. A missing, empty, truncated, or otherwise
    /// unparseable file yields defaults rather than an exception: losing your
    /// window position is a far better outcome than an app that will not start.
    /// </summary>
    public AppSettings Load()
    {
        var path = ResolveReadPath();
        if (path is null)
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();

            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                // Write the old name's settings out under the new one now rather than
                // waiting for something to change. Left lazy, an install that was
                // never touched again would still depend on the old folder being
                // there, which is a thing a tidy-minded person deletes.
                Save(settings);
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read settings from {path}, falling back to defaults.", ex);
            return new AppSettings();
        }
    }

    /// <summary>The file to read: this app's own, or the old name's as a fallback.</summary>
    private string? ResolveReadPath()
    {
        if (File.Exists(_path))
        {
            return _path;
        }

        if (_legacyPath is not null && File.Exists(_legacyPath))
        {
            Log.Info($"No settings at {_path} yet, so reading the ones left by the old name at {_legacyPath}.");
            return _legacyPath;
        }

        return null;
    }

    public void Save(AppSettings settings)
    {
        lock (_writeLock)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(settings, SerializerOptions);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save settings to {_path}.", ex);
            }
        }
    }
}
