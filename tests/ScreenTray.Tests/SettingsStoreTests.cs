using System.IO;
using ScreenTray.Models;
using ScreenTray.Services;
using Xunit;

namespace ScreenTray.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ScreenTrayTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Load_returns_defaults_when_there_is_no_file_yet()
    {
        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Null(settings.WatchedFolder);
        Assert.Equal(AppSettings.DefaultKeepCount, settings.KeepCount);
        Assert.Equal(AppThemeMode.System, settings.ThemeMode);
        Assert.Empty(settings.RotationApprovedFolders);

        // Autostart is on by default: the app is no use if it is not already there
        // when you take a screenshot.
        Assert.True(settings.RunAtLogin);

        // And a fresh install opens the strip rather than hiding in the tray.
        Assert.False(settings.ClosedToTray);
    }

    [Fact]
    public void The_closed_to_tray_state_round_trips_so_signing_in_restores_it()
    {
        var store = new SettingsStore(SettingsPath);

        store.Save(new AppSettings { WatchedFolder = @"C:\shots", ClosedToTray = true });
        Assert.True(store.Load().ClosedToTray);

        store.Save(new AppSettings { WatchedFolder = @"C:\shots", ClosedToTray = false });
        Assert.False(store.Load().ClosedToTray);
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var store = new SettingsStore(SettingsPath);

        var saved = new AppSettings
        {
            WatchedFolder = @"C:\Users\someone\Pictures\Screenshots",
            KeepCount = 20,
            RunAtLogin = true,
            ThemeMode = AppThemeMode.Dark,
            Placement = new WindowPlacementData { Left = 10, Top = 20, Right = 930, Bottom = 220, ShowCommand = 1 },
        };
        saved.ApproveRotation(@"C:\Users\someone\Pictures\Screenshots");

        store.Save(saved);
        var loaded = store.Load();

        Assert.Equal(saved.WatchedFolder, loaded.WatchedFolder);
        Assert.Equal(20, loaded.KeepCount);
        Assert.True(loaded.RunAtLogin);
        Assert.Equal(AppThemeMode.Dark, loaded.ThemeMode);
        Assert.Equal(930, loaded.Placement!.Right);
        Assert.True(loaded.HasApprovedRotation(@"c:\users\someone\pictures\screenshots"));
    }

    [Fact]
    public void Load_falls_back_to_defaults_when_the_file_is_truncated()
    {
        // What a crash part-way through a write would leave behind. Losing the
        // window position beats refusing to start.
        System.IO.File.WriteAllText(SettingsPath, "{ \"watchedFolder\": \"C:\\\\shots\", \"keepCou");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(AppSettings.DefaultKeepCount, settings.KeepCount);
    }

    [Fact]
    public void Load_falls_back_to_defaults_when_the_file_is_not_json_at_all()
    {
        System.IO.File.WriteAllText(SettingsPath, "this is not json");

        Assert.Equal(AppSettings.DefaultKeepCount, new SettingsStore(SettingsPath).Load().KeepCount);
    }

    [Fact]
    public void Load_falls_back_to_defaults_when_the_file_is_empty()
    {
        System.IO.File.WriteAllText(SettingsPath, string.Empty);

        Assert.Equal(AppSettings.DefaultKeepCount, new SettingsStore(SettingsPath).Load().KeepCount);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { WatchedFolder = @"C:\one" });
        store.Save(new AppSettings { WatchedFolder = @"C:\two" });

        Assert.False(System.IO.File.Exists(SettingsPath + ".tmp"));
        Assert.Equal(@"C:\two", store.Load().WatchedFolder);
    }

    [Theory]
    [InlineData(0, AppSettings.MinKeepCount)]
    [InlineData(1, AppSettings.MinKeepCount)]
    [InlineData(12, 12)]
    [InlineData(500, AppSettings.MaxKeepCount)]
    [InlineData(-5, AppSettings.MinKeepCount)]
    public void NormalisedKeepCount_clamps_nonsense_into_range(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { KeepCount = stored }.NormalisedKeepCount());

    [Fact]
    public void ApproveRotation_does_not_add_the_same_folder_twice()
    {
        var settings = new AppSettings();
        settings.ApproveRotation(@"C:\shots");
        settings.ApproveRotation(@"C:\SHOTS");

        Assert.Single(settings.RotationApprovedFolders);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}
