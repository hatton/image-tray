using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ScreenTray.Models;

namespace ScreenTray.Services;

/// <summary>
/// Saves and restores where the window was, using the struct Windows itself hands
/// out.
/// </summary>
/// <remarks>
/// Persisting <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> in WPF's
/// device-independent units is the usual approach and the usual source of bugs:
/// the numbers are relative to a DPI that may have changed, so a window saved on a
/// 150% display comes back the wrong size on a 100% one. GetWindowPlacement and
/// SetWindowPlacement round-trip the same physical values Windows uses
/// internally, so restoring is exact.
/// </remarks>
public static partial class WindowPlacementService
{
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;

    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint SPI_GETWORKAREA = 0x0030;

    /// <summary>
    /// How much of the window has to land on a monitor's work area for the saved
    /// spot to count as usable. A window with only a sliver on screen is as good
    /// as lost.
    /// </summary>
    private const int MinVisibleWidth = 160;
    private const int MinVisibleHeight = 60;

    /// <summary>Reads the current placement. Returns null before the window has a handle.</summary>
    public static WindowPlacementData? Capture(Window window)
    {
        var handle = HandleOf(window);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(handle, ref placement))
        {
            Log.Warn("GetWindowPlacement failed; the window position will not be remembered this time.");
            return null;
        }

        // Coming back minimised would be a hostile way to reopen. Normal is the
        // only state worth remembering, alongside maximised.
        var showCommand = placement.showCmd == SW_SHOWMINIMIZED ? SW_SHOWNORMAL : placement.showCmd;

        return new WindowPlacementData
        {
            Left = placement.rcNormalPosition.Left,
            Top = placement.rcNormalPosition.Top,
            Right = placement.rcNormalPosition.Right,
            Bottom = placement.rcNormalPosition.Bottom,
            ShowCommand = showCommand,
        };
    }

    /// <summary>
    /// Restores a saved placement. Returns false when the saved spot is unusable,
    /// which is the caller's cue to fall back to a default position.
    /// </summary>
    public static bool TryApply(Window window, WindowPlacementData? data)
    {
        if (data is null || !IsUsable(data))
        {
            return false;
        }

        var handle = HandleOf(window);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            flags = 0,
            showCmd = data.ShowCommand == SW_SHOWMAXIMIZED ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
            ptMinPosition = new POINT { X = -1, Y = -1 },
            ptMaxPosition = new POINT { X = -1, Y = -1 },
            rcNormalPosition = new RECT
            {
                Left = data.Left,
                Top = data.Top,
                Right = data.Right,
                Bottom = data.Bottom,
            },
        };

        if (!SetWindowPlacement(handle, ref placement))
        {
            Log.Warn("SetWindowPlacement failed; falling back to a default position.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when enough of the saved rectangle lands on some monitor's work area to
    /// be worth restoring. False after the display it lived on has been unplugged
    /// or rearranged.
    /// </summary>
    public static bool IsUsable(WindowPlacementData data)
    {
        var width = data.Right - data.Left;
        var height = data.Bottom - data.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // rcNormalPosition is in workspace coordinates, which differ from screen
        // coordinates by the primary monitor's work area origin. That offset is
        // zero for the usual bottom taskbar and non-zero for a top or left one, so
        // it has to be added before asking which monitor the rectangle is on.
        var offset = PrimaryWorkAreaOrigin();
        var rect = new RECT
        {
            Left = data.Left + offset.X,
            Top = data.Top + offset.Y,
            Right = data.Right + offset.X,
            Bottom = data.Bottom + offset.Y,
        };

        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            // The rectangle is on a monitor even if we could not measure it.
            return true;
        }

        var visibleWidth = Math.Min(rect.Right, info.rcWork.Right) - Math.Max(rect.Left, info.rcWork.Left);
        var visibleHeight = Math.Min(rect.Bottom, info.rcWork.Bottom) - Math.Max(rect.Top, info.rcWork.Top);

        return visibleWidth >= Math.Min(MinVisibleWidth, width)
            && visibleHeight >= Math.Min(MinVisibleHeight, height);
    }

    /// <summary>
    /// Where the window goes when there is nothing usable to restore: centred
    /// horizontally and sitting just above the taskbar on the primary display,
    /// which is where a screenshot strip wants to live.
    /// </summary>
    public static void PlaceAtPrimaryBottom(Window window)
    {
        var work = SystemParameters.WorkArea;

        window.Width = Math.Min(window.Width, Math.Max(window.MinWidth, work.Width - 80));
        window.Left = work.Left + ((work.Width - window.Width) / 2);
        window.Top = Math.Max(work.Top, work.Bottom - window.Height - 24);
    }

    private static IntPtr HandleOf(Window window) =>
        PresentationSource.FromVisual(window) is HwndSource source ? source.Handle : IntPtr.Zero;

    private static POINT PrimaryWorkAreaOrigin()
    {
        var work = new RECT();
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, ref work, 0))
        {
            return new POINT { X = work.Left, Y = work.Top };
        }

        return new POINT { X = 0, Y = 0 };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
}
