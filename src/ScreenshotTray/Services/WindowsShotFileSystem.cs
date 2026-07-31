using System.IO;
using System.Runtime.InteropServices;

namespace ScreenshotTray.Services;

/// <summary>
/// The real file system. Recycling goes through SHFileOperation with
/// FOF_ALLOWUNDO, which is the shell's own "send to Recycle Bin" path, so the
/// files show up recoverable exactly as if you had pressed Delete in Explorer.
/// </summary>
public sealed partial class WindowsShotFileSystem : IShotFileSystem
{
    /// <summary>
    /// Extensions the app treats as screenshots. WebP is deliberately absent:
    /// WIC only decodes it when the optional Windows codec is installed, so
    /// including it would mean thumbnails that sometimes silently fail.
    /// </summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public bool DirectoryExists(string folder) => Directory.Exists(folder);

    public IReadOnlyList<ShotFile> EnumerateImageFiles(string folder)
    {
        var results = new List<ShotFile>();

        try
        {
            // Top level only. Subdirectories are never enumerated and never
            // touched, so a folder of screenshots with an "archive" subfolder in
            // it keeps that subfolder to itself.
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsImageFile(path))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    results.Add(new ShotFile(path, info.LastWriteTimeUtc, info.Length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished between the enumeration and the stat
                    // call is not interesting. Skip it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not enumerate {folder}.", ex);
        }

        return results;
    }

    public void RecycleFile(string path)
    {
        // pFrom is a double-null-terminated list of null-terminated paths.
        var from = path + '\0' + '\0';

        var operation = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = from,
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMMKDIR,
        };

        var result = SHFileOperationW(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
        {
            throw new IOException(
                $"Could not send '{path}' to the Recycle Bin (SHFileOperation returned 0x{result:X8}" +
                (operation.fAnyOperationsAborted ? ", aborted" : string.Empty) + ").");
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
