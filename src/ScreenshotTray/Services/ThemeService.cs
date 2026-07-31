using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using ScreenshotTray.Models;

namespace ScreenshotTray.Services;

/// <summary>
/// Swaps between the light and dark palettes and keeps the window's title-bar
/// frame in step.
/// </summary>
/// <remarks>
/// This app defines its own two-palette theme rather than using WPF's Fluent
/// theme. The strip is almost entirely custom-drawn, so a stock control theme buys
/// nothing, and the palette here is a dozen brushes that both themes define under
/// the same keys. Everything binds with DynamicResource, so swapping the
/// dictionary restyles the live window with no restart.
/// </remarks>
public static partial class ThemeService
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Uri LightUri = new("/Theme/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("/Theme/Dark.xaml", UriKind.Relative);

    /// <summary>Whether the palette currently in use is the dark one.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Reads the Windows "app mode" preference.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            // AppsUseLightTheme is 1 for light and 0 for dark. A missing value means
            // an older Windows that only did light.
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the system theme preference; assuming light.", ex);
            return false;
        }
    }

    public static bool ResolveDark(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => false,
        AppThemeMode.Dark => true,
        _ => SystemPrefersDark(),
    };

    /// <summary>
    /// Installs the palette for <paramref name="mode"/> as the application's first
    /// merged dictionary.
    /// </summary>
    public static void Apply(AppThemeMode mode)
    {
        var dark = ResolveDark(mode);
        IsDark = dark;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = dark ? DarkUri : LightUri };

        if (dictionaries.Count == 0)
        {
            dictionaries.Add(palette);
        }
        else
        {
            dictionaries[0] = palette;
        }

        foreach (var window in Application.Current.Windows.OfType<Window>())
        {
            ApplyWindowFrame(window);
        }
    }

    /// <summary>
    /// Tells the compositor to draw this window's frame dark. Without it a dark
    /// strip gets a bright border and shadow, which looks like a bug.
    /// </summary>
    public static void ApplyWindowFrame(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source || source.Handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(source.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
