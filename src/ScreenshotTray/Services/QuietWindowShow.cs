using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenshotTray.Services;

/// <summary>
/// Shows a window and raises it to the front without giving it focus.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "pop up when a screenshot arrives" tolerable rather than
/// infuriating. You are usually mid-sentence in another app when you take a
/// screenshot, and a window that steals focus at that moment eats your next
/// keystrokes, or worse, sends them somewhere they do something.
/// </para>
/// <para>
/// WPF's <see cref="Window.Show"/> activates by default, so the window is created
/// with <c>ShowActivated</c> off and then raised with Win32 directly.
/// <c>SW_SHOWNOACTIVATE</c> makes it visible without activation, and the
/// topmost-then-not-topmost pair is the documented way to lift a window to the top
/// of the z-order while <c>SWP_NOACTIVATE</c> keeps focus where it was.
/// </para>
/// </remarks>
public static partial class QuietWindowShow
{
    private const int SW_SHOWNOACTIVATE = 4;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    public static void ShowWithoutActivating(Window window)
    {
        // WPF has to believe the window is visible, or layout and rendering never
        // run and the strip appears blank.
        window.ShowActivated = false;

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (PresentationSource.FromVisual(window) is not HwndSource source || source.Handle == IntPtr.Zero)
        {
            return;
        }

        var handle = source.Handle;

        ShowWindow(handle, SW_SHOWNOACTIVATE);

        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Dropping straight back out of topmost leaves the window at the front
        // without pinning it above everything else from now on.
        if (!window.Topmost)
        {
            SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
