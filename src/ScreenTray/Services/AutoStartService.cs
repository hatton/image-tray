using Microsoft.Win32;

namespace ScreenTray.Services;

/// <summary>
/// Registers the app to start when you sign in, via the per-user Run key. That
/// key needs no elevation, which is why it beats a scheduled task or a machine
/// wide entry for something like this.
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenTray";

    /// <summary>
    /// Marks a launch as coming from signing in rather than from you starting the
    /// app yourself. Only a login launch consults the remembered visibility;
    /// double-clicking the exe always shows the window, because that is plainly
    /// what you meant by double-clicking it.
    /// </summary>
    public const string AutoStartArgument = "--autostart";

    /// <summary>True when the Run entry exists and points at this exact executable.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var existing = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(existing);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the autostart registry value.", ex);
            return false;
        }
    }

    /// <summary>
    /// True when autostart is on and the recorded path still matches where the app
    /// is now. A false here after <see cref="IsEnabled"/> returns true means the
    /// exe has moved and the entry needs rewriting.
    /// </summary>
    public static bool IsCurrent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var existing = key?.GetValue(ValueName) as string;
            return string.Equals(existing, CommandLine(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the autostart registry value.", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                Log.Warn("Could not open the Run key, so autostart was left unchanged.");
                return;
            }

            if (enabled)
            {
                key.SetValue(ValueName, CommandLine(), RegistryValueKind.String);
                Log.Info("Autostart enabled.");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Autostart disabled.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not {(enabled ? "enable" : "disable")} autostart.", ex);
        }
    }

    private static string CommandLine()
    {
        // Environment.ProcessPath is the real exe even under single-file publish,
        // where Assembly.Location is empty.
        var exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        return $"\"{exe}\" {AutoStartArgument}";
    }
}
