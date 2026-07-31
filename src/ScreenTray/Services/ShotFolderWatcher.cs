using System.IO;
using ScreenTray.Models;

namespace ScreenTray.Services;

/// <summary>The outcome of one folder scan.</summary>
/// <param name="Folder">The folder that was scanned.</param>
/// <param name="FolderExists">False when the folder has been renamed or removed.</param>
/// <param name="AllFiles">Every image file in the folder, newest first. What rotation works from.</param>
/// <param name="Shots">The newest files, capped at the display count, with dimensions where known.</param>
public sealed record FolderScan(
    string Folder,
    bool FolderExists,
    IReadOnlyList<ShotFile> AllFiles,
    IReadOnlyList<Shot> Shots);

/// <summary>
/// Watches a folder for screenshots and reports the newest ones.
/// </summary>
/// <remarks>
/// Two things make this less trivial than it looks. A single file write raises
/// several <see cref="FileSystemWatcher"/> events, so events are coalesced onto a
/// short timer and each tick does a fresh scan instead of trying to track
/// individual changes. And the capture tool is often still writing the file when
/// the first event arrives, so a file whose header cannot be read yet is still
/// listed but schedules a follow-up scan.
/// </remarks>
public sealed class ShotFolderWatcher : IDisposable
{
    private const int DebounceMs = 250;
    private const int PendingRetryMs = 250;
    private const int MaxPendingRetries = 8;

    private readonly object _gate = new();
    private readonly System.Threading.Timer _debounce;

    private FileSystemWatcher? _watcher;
    private string? _folder;
    private int _pendingRetries;
    private bool _disposed;

    public ShotFolderWatcher() =>
        _debounce = new System.Threading.Timer(_ => RunScan(), state: null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Raised on a background thread after every scan.</summary>
    public event EventHandler<FolderScan>? Scanned;

    /// <summary>
    /// How many of the newest files get dimensions read and turned into
    /// <see cref="Shot"/> records. Kept in step with the keep count.
    /// </summary>
    public int DisplayCount { get; set; } = AppSettings.DefaultKeepCount;

    public IShotFileSystem FileSystem { get; init; } = new WindowsShotFileSystem();

    public string? Folder
    {
        get { lock (_gate) { return _folder; } }
    }

    /// <summary>Points the watcher at a folder and scans it immediately.</summary>
    public void Watch(string folder)
    {
        lock (_gate)
        {
            TearDownWatcher();
            _folder = folder;

            if (FileSystem.DirectoryExists(folder))
            {
                try
                {
                    _watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };

                    _watcher.Created += OnChanged;
                    _watcher.Deleted += OnChanged;
                    _watcher.Changed += OnChanged;
                    _watcher.Renamed += OnChanged;
                    _watcher.Error += OnWatcherError;
                    _watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not watch '{folder}'. The tray will still work, refreshed manually.", ex);
                    TearDownWatcher();
                }
            }
        }

        RequestScan(immediate: true);
    }

    public void Stop()
    {
        lock (_gate)
        {
            TearDownWatcher();
            _folder = null;
        }

        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Queues a scan, coalescing with any already queued.</summary>
    public void RequestScan(bool immediate = false)
    {
        if (_disposed)
        {
            return;
        }

        _pendingRetries = 0;
        _debounce.Change(immediate ? 0 : DebounceMs, Timeout.Infinite);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Renamed || WindowsShotFileSystem.IsImageFile(e.Name ?? e.FullPath))
        {
            RequestScan();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The usual cause is the internal buffer overflowing during a burst, or
        // the folder disappearing. Rebuilding the watcher recovers from both.
        Log.Warn("The folder watcher faulted; rebuilding it.", e.GetException());

        string? folder;
        lock (_gate)
        {
            folder = _folder;
        }

        if (folder is not null)
        {
            Watch(folder);
        }
    }

    private void RunScan()
    {
        string? folder;
        int displayCount;

        lock (_gate)
        {
            folder = _folder;
            displayCount = Math.Max(1, DisplayCount);
        }

        if (folder is null || _disposed)
        {
            return;
        }

        if (!FileSystem.DirectoryExists(folder))
        {
            Scanned?.Invoke(this, new FolderScan(folder, FolderExists: false, [], []));
            return;
        }

        var ordered = RotationPlanner.OrderNewestFirst(FileSystem.EnumerateImageFiles(folder));

        var shots = new List<Shot>(Math.Min(displayCount, ordered.Count));
        var anyPending = false;

        foreach (var file in ordered.Take(displayCount))
        {
            // A file whose header will not parse yet is still listed, with a
            // placeholder aspect ratio, so a screenshot never goes missing from
            // the tray just because we caught it mid-write.
            var known = ImageHeader.TryReadPixelSize(file.Path, out var width, out var height);
            anyPending |= !known;
            shots.Add(new Shot(file.Path, file.LastWriteUtc, file.Length, width, height));
        }

        Scanned?.Invoke(this, new FolderScan(folder, FolderExists: true, ordered, shots));

        if (anyPending && _pendingRetries < MaxPendingRetries && !_disposed)
        {
            _pendingRetries++;
            _debounce.Change(PendingRetryMs, Timeout.Infinite);
        }
    }

    private void TearDownWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Changed -= OnChanged;
        _watcher.Renamed -= OnChanged;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _debounce.Dispose();
    }
}
