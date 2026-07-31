using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTray.Models;

namespace ScreenTray.Services;

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
    private readonly object _writeLock = new();

    public SettingsStore()
        : this(DefaultPath())
    {
    }

    public SettingsStore(string path) => _path = path;

    public string Path => _path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenTray",
        "settings.json");

    /// <summary>
    /// Reads the settings file. A missing, empty, truncated, or otherwise
    /// unparseable file yields defaults rather than an exception: losing your
    /// window position is a far better outcome than an app that will not start.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read settings from {_path}, falling back to defaults.", ex);
            return new AppSettings();
        }
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
