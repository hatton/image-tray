using System.Diagnostics;
using System.IO;

namespace ScreenTray.Services;

/// <summary>
/// A deliberately tiny logger. This app has no server to phone home to and no
/// console to print at, so warnings land in a rolling file next to the settings
/// and in the debugger output.
/// </summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenTray",
        "screentray.log");

    public static void Info(string message) => Write("INFO ", message, exception: null);

    public static void Warn(string message, Exception? exception = null) => Write("WARN ", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + "        " + exception;
        }

        Debug.WriteLine(line);

        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Truncate rather than rotate. Nobody is going to read week-old
                // logs from a screenshot tray, and an unbounded file is rude.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never be the thing that breaks the app.
            }
        }
    }
}
