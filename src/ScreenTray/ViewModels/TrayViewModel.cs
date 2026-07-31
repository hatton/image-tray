using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTray.Models;
using ScreenTray.Services;

namespace ScreenTray.ViewModels;

/// <summary>
/// Holds the strip together: watches the folder, keeps the tile list in step,
/// drives thumbnail decoding at the current height, and runs rotation.
/// </summary>
public sealed partial class TrayViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Below this many files, the first rotation in an unfamiliar folder happens
    /// without asking. Recycling a couple of stale screenshots is what the app is
    /// for; recycling ninety of them without warning is not.
    /// </summary>
    private const int BulkRotationPromptThreshold = 3;

    /// <summary>How long the height has to stop changing before thumbnails re-decode.</summary>
    private static readonly TimeSpan HeightSettleDelay = TimeSpan.FromMilliseconds(140);

    private readonly SettingsStore _store;
    private readonly IShotFileSystem _fileSystem;
    private readonly ShotFolderWatcher _watcher;
    private readonly ThumbnailCache _thumbnails = new();
    private readonly RotationService _rotation;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _heightSettle;
    private readonly HashSet<string> _rotationDeclined = new(StringComparer.OrdinalIgnoreCase);

    private bool _hasScannedOnce;
    private bool _disposed;

    [ObservableProperty]
    private string? _watchedFolder;

    [ObservableProperty]
    private bool _folderMissing;

    [ObservableProperty]
    private double _tileHeight = 152;

    public TrayViewModel(SettingsStore store, AppSettings settings, IShotFileSystem? fileSystem = null)
    {
        _store = store;
        Settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _fileSystem = fileSystem ?? new WindowsShotFileSystem();
        _rotation = new RotationService(_fileSystem);
        _watcher = new ShotFolderWatcher { FileSystem = _fileSystem, DisplayCount = settings.NormalisedKeepCount() };
        _watcher.Scanned += OnScanned;

        _heightSettle = new DispatcherTimer { Interval = HeightSettleDelay };
        _heightSettle.Tick += (_, _) =>
        {
            _heightSettle.Stop();
            LoadThumbnails(missingOnly: false);
        };

        Shots.CollectionChanged += OnShotsChanged;
        _watchedFolder = settings.WatchedFolder;
    }

    /// <summary>
    /// Asks the user for a folder, returning null if they cancel. Supplied by the
    /// view, because a view model has no business owning a dialog.
    /// </summary>
    public Func<string?, string?>? RequestFolderChoice { get; set; }

    /// <summary>
    /// Asks whether a first bulk rotation in this folder is wanted, given how many
    /// files it would recycle.
    /// </summary>
    public Func<string, int, bool>? ConfirmBulkRotation { get; set; }

    /// <summary>
    /// Raised when a screenshot arrives while the app is watching, and only when the
    /// setting asks for it. Never raised for the initial scan at startup.
    /// </summary>
    public event EventHandler? NewScreenshotArrived;

    public AppSettings Settings { get; }

    public ObservableCollection<ShotViewModel> Shots { get; } = [];

    public bool HasShots => Shots.Count > 0;

    /// <summary>The shape of every thumbnail cell, width over height.</summary>
    public double CellAspect => Settings.CellAspect;

    /// <summary>
    /// Sets the cell shape from a desired pixel width, which is what the splitter drag
    /// produces. Stored as a ratio so cells keep their shape as the tray is resized.
    /// </summary>
    /// <param name="cellWidth">The width one cell should have at the current tray height.</param>
    /// <param name="persist">
    /// False while the drag is in flight, so the settings file is written once at the
    /// end rather than on every mouse-move.
    /// </param>
    public void ApplyCellWidth(double cellWidth, bool persist)
    {
        var height = Math.Max(TileMetrics.MinHeight, TileHeight);
        var aspect = TileMetrics.ClampCellAspect(cellWidth / height);

        if (Math.Abs(aspect - Settings.CellAspect) > 0.0005)
        {
            Settings.CellAspect = aspect;
            OnPropertyChanged(nameof(CellAspect));
            LoadThumbnails(missingOnly: false);
        }

        if (persist)
        {
            _store.Save(Settings);
        }
    }

    /// <summary>Sets the cell shape directly, from the slider in Settings.</summary>
    public void ApplyCellAspect(double cellAspect)
    {
        Settings.CellAspect = TileMetrics.ClampCellAspect(cellAspect);
        _store.Save(Settings);
        OnPropertyChanged(nameof(CellAspect));
        LoadThumbnails(missingOnly: false);
    }


    /// <summary>
    /// Just the folder's own name for the title bar. The full path is long enough to
    /// swamp a 30px strip, and it lives in the tooltip instead.
    /// </summary>
    public string? WatchedFolderName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WatchedFolder))
            {
                return null;
            }

            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(WatchedFolder);
            var name = System.IO.Path.GetFileName(trimmed);

            // A drive root has no file name component, so fall back to the path.
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    public string EmptyTitle => FolderMissing
        ? "That folder has gone"
        : WatchedFolder is null
            ? "No folder chosen yet"
            : "No screenshots yet";

    public string EmptyDetail => FolderMissing
        ? $"{WatchedFolder} is no longer there. Choose another folder to watch."
        : WatchedFolder is null
            ? "Pick the folder your screenshot tool saves into."
            : "Take a screenshot and it will appear here.";

    /// <summary>Begins watching whatever folder the settings point at.</summary>
    public void Start()
    {
        if (string.IsNullOrWhiteSpace(Settings.WatchedFolder))
        {
            WatchedFolder = null;
            FolderMissing = false;
            RaiseEmptyStateChanged();
            return;
        }

        WatchedFolder = Settings.WatchedFolder;
        _watcher.DisplayCount = Settings.NormalisedKeepCount();
        _watcher.Watch(Settings.WatchedFolder);
    }

    /// <summary>Points the app at a different folder and persists the choice.</summary>
    public void SetFolder(string folder)
    {
        Settings.WatchedFolder = folder;
        _store.Save(Settings);

        _rotationDeclined.Remove(folder);
        _thumbnails.Clear();
        _hasScannedOnce = false;
        Shots.Clear();

        Start();
    }

    /// <summary>Re-reads settings that affect watching, after the settings dialog closes.</summary>
    public void ApplyChangedSettings()
    {
        _watcher.DisplayCount = Settings.NormalisedKeepCount();
        _store.Save(Settings);
        _watcher.RequestScan(immediate: true);
    }

    public void SaveSettings() => _store.Save(Settings);

    [RelayCommand]
    public void ChooseFolder()
    {
        var chosen = RequestFolderChoice?.Invoke(WatchedFolder);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            SetFolder(chosen);
        }
    }

    [RelayCommand]
    private void Refresh() => _watcher.RequestScan(immediate: true);

    /// <summary>
    /// Takes the pill off every tile. Called when the window loses focus, which is
    /// the point at which "this is what is on your clipboard" stops being a useful
    /// thing to be told.
    /// </summary>
    public void ClearCopyBadges()
    {
        foreach (var tile in Shots)
        {
            tile.CopyBadge = null;
        }
    }

    /// <summary>
    /// Only one screenshot can be on the clipboard, so only one tile wears the pill.
    /// The tile that was just copied set its own; this clears everyone else's.
    /// </summary>
    /// <summary>
    /// Sends one screenshot to the Recycle Bin from its trash button. The tile goes
    /// straight away rather than waiting for the folder watcher, so the click feels
    /// immediate; the rescan then backfills whatever was sitting just outside the
    /// keep window.
    /// </summary>
    private void OnTileDeleteRequested(object? sender, EventArgs e)
    {
        if (sender is not ShotViewModel tile)
        {
            return;
        }

        try
        {
            _fileSystem.RecycleFile(tile.Path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not send '{tile.Path}' to the Recycle Bin.", ex);
            tile.CopyBadge = "Delete failed";
            return;
        }

        _thumbnails.Forget(tile.Path);
        Detach(tile);
        Shots.Remove(tile);
        _watcher.RequestScan();
    }

    private void Detach(ShotViewModel tile)
    {
        tile.CopyFeedbackRequested -= OnTileCopied;
        tile.DeleteRequested -= OnTileDeleteRequested;
    }

    private void OnTileCopied(object? sender, string message)
    {
        foreach (var tile in Shots)
        {
            if (!ReferenceEquals(tile, sender))
            {
                tile.CopyBadge = null;
            }
        }
    }

    partial void OnTileHeightChanged(double value)
    {
        // Fill in anything with no thumbnail at all straight away so the strip
        // paints, then re-decode everything at the new size once the drag stops.
        LoadThumbnails(missingOnly: true);
        _heightSettle.Stop();
        _heightSettle.Start();
    }

    partial void OnFolderMissingChanged(bool value) => RaiseEmptyStateChanged();

    partial void OnWatchedFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(WatchedFolderName));
        RaiseEmptyStateChanged();
    }

    private void OnShotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasShots));
        RaiseEmptyStateChanged();
    }

    private void RaiseEmptyStateChanged()
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));
    }

    /// <summary>Scan results arrive on a background thread and are handled on the UI one.</summary>
    private void OnScanned(object? sender, FolderScan scan)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.InvokeAsync(() => HandleScan(scan));
    }

    private void HandleScan(FolderScan scan)
    {
        if (_disposed || !string.Equals(scan.Folder, Settings.WatchedFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!scan.FolderExists)
        {
            FolderMissing = true;
            Shots.Clear();
            return;
        }

        FolderMissing = false;
        var arrived = Reconcile(scan.Shots);
        LoadThumbnails(missingOnly: true);
        _hasScannedOnce = true;

        RotateIfWanted(scan);

        if (arrived && Settings.ShowOnNewScreenshot)
        {
            NewScreenshotArrived?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RotateIfWanted(FolderScan scan)
    {
        var keepCount = Settings.NormalisedKeepCount();
        var pending = _rotation.CountPendingRemoval(scan.AllFiles, keepCount);
        if (pending == 0 || _rotationDeclined.Contains(scan.Folder))
        {
            return;
        }

        if (!Settings.HasApprovedRotation(scan.Folder) && pending > BulkRotationPromptThreshold)
        {
            var approved = ConfirmBulkRotation?.Invoke(scan.Folder, pending) ?? false;
            if (!approved)
            {
                // Remember the refusal for this session so the prompt does not
                // reappear on every single scan.
                _rotationDeclined.Add(scan.Folder);
                Log.Info($"Rotation declined for '{scan.Folder}'; leaving {pending} older screenshot(s) alone.");
                return;
            }
        }

        Settings.ApproveRotation(scan.Folder);
        _store.Save(Settings);

        var doomed = RotationPlanner.SelectForRemoval(scan.AllFiles, keepCount);
        var result = _rotation.Rotate(scan.AllFiles, keepCount);

        if (result.Recycled > 0)
        {
            foreach (var file in doomed)
            {
                _thumbnails.Forget(file.Path);
            }

            _watcher.RequestScan();
        }
    }

    /// <summary>
    /// Brings the tile list in line with a scan while keeping the existing tiles.
    /// Reusing view models matters: a tile that is mid-animation must not be torn
    /// down and rebuilt just because the folder was rescanned.
    /// </summary>
    /// <returns>
    /// True when a screenshot appeared that was not there before, and this was not
    /// the first scan. A rescan triggered by a file finishing its write, or by
    /// rotation, adds nothing and so reports false.
    /// </returns>
    private bool Reconcile(IReadOnlyList<Shot> shots)
    {
        var arrived = false;

        var incoming = new HashSet<string>(shots.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);

        for (var i = Shots.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Shots[i].Path))
            {
                Detach(Shots[i]);
                _thumbnails.Forget(Shots[i].Path);
                Shots.RemoveAt(i);
            }
        }

        var byPath = Shots.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];

            if (byPath.TryGetValue(shot.Path, out var existing))
            {
                existing.Update(shot);

                var currentIndex = Shots.IndexOf(existing);
                if (currentIndex != i)
                {
                    Shots.Move(currentIndex, i);
                }

                continue;
            }

            // IsNew only on a screenshot that showed up while we were watching, so
            // the first paint at startup does not flash every tile at once.
            var added = new ShotViewModel(shot) { IsNew = _hasScannedOnce };
            added.CopyFeedbackRequested += OnTileCopied;
            added.DeleteRequested += OnTileDeleteRequested;
            Shots.Insert(i, added);
            byPath[shot.Path] = added;

            arrived |= _hasScannedOnce;
        }

        return arrived;
    }

    private void LoadThumbnails(bool missingOnly)
    {
        foreach (var tile in Shots.ToList())
        {
            if (missingOnly && tile.Thumbnail is not null)
            {
                continue;
            }

            var bucket = BucketFor(tile);

            if (!tile.NeedsThumbnail(bucket))
            {
                continue;
            }

            _ = LoadThumbnailAsync(tile, tile.Shot, bucket);
        }
    }

    /// <summary>
    /// The decode height for one tile. Capped at the screenshot's own height for the
    /// same reason the tile is: decoding a 60px-tall capture at 340px would just
    /// produce a bigger, blurrier bitmap.
    /// </summary>
    /// <summary>
    /// The decode height for one tile: the height of the image inside its cell, not
    /// the cell's own height. A wide screenshot only occupies a band of its cell, and
    /// decoding it at the full cell height would waste most of those pixels.
    /// </summary>
    private int BucketFor(ShotViewModel tile)
    {
        var cell = TileMetrics.ResolveCell(TileHeight, CellAspect);
        return ThumbnailCache.BucketFor(TileMetrics.ResolveImageBox(cell, tile.AspectRatio).Height);
    }

    private async Task LoadThumbnailAsync(ShotViewModel tile, Shot shot, int bucket)
    {
        var image = await _thumbnails
            .GetAsync(shot.Path, shot.LastWriteUtc, bucket)
            .ConfigureAwait(false);

        if (image is null || _disposed)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            // The tile may have been replaced, or the height may have moved on. A
            // stale decode is still worth showing when there is nothing at all,
            // but it must never replace a sharper one.
            if (!string.Equals(tile.Shot.Path, shot.Path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var wanted = BucketFor(tile);
            if (bucket == wanted || tile.Thumbnail is null)
            {
                tile.SetThumbnail(image, bucket);
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _heightSettle.Stop();
        _watcher.Scanned -= OnScanned;
        _watcher.Dispose();
        _thumbnails.Clear();
    }
}
