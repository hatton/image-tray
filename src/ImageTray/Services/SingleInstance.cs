using System.Diagnostics;
using System.IO;

namespace ImageTray.Services;

/// <summary>
/// What a launch found when it went looking for the single-instance slot.
/// </summary>
public enum LaunchOutcome
{
    /// <summary>The slot is ours. Carry on starting up.</summary>
    Claimed,

    /// <summary>The same exe was already running and has been asked to surface.</summary>
    SurfacedExisting,

    /// <summary>A different build holds the slot and would not give it up.</summary>
    BlockedByOtherBuild,
}

/// <summary>An exe that is already running, and when it was built.</summary>
public sealed record RunningBuild(string Path, DateTime BuiltAt);

/// <summary>The outcome of a launch, plus whatever it needs to explain itself.</summary>
public sealed record LaunchResult(LaunchOutcome Outcome, SingleInstance? Instance, RunningBuild? Other);

/// <summary>
/// Keeps one copy of the app running per signed-in user, and turns a second launch
/// into "show the window I already have".
/// </summary>
/// <remarks>
/// This matters more than it sounds. The app lives in the tray, so double-clicking
/// the exe out of habit is a completely reasonable way to try to get at it, and
/// without this it would start a second tray icon fighting the first over the same
/// folder.
///
/// The slot is keyed on a name rather than on the exe, so the instance already
/// running can perfectly well be a different build. Surfacing it silently in that
/// case is how someone ends up looking at an old window and concluding their change
/// did not work, so a mismatch is never silent. A debug build takes the slot over,
/// on the grounds that a build fresh out of the compiler exists to be looked at.
/// Any other build reports the collision and stops.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ImageTray.SingleInstance";
    private const string SurfaceSignalName = @"Local\ImageTray.ShowWindow";
    private const string QuitSignalName = @"Local\ImageTray.Quit";

    /// <summary>How long to give a replaced instance to let go of the slot.</summary>
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _surfaceSignal;
    private readonly EventWaitHandle _quitSignal;
    private readonly CancellationTokenSource _listenerCancellation;

    private SingleInstance(
        Mutex mutex,
        EventWaitHandle surfaceSignal,
        EventWaitHandle quitSignal,
        CancellationTokenSource listenerCancellation)
    {
        _mutex = mutex;
        _surfaceSignal = surfaceSignal;
        _quitSignal = quitSignal;
        _listenerCancellation = listenerCancellation;
    }

    /// <summary>
    /// Claims the single-instance slot, or works out why it could not.
    /// </summary>
    /// <param name="onSurfaceRequest">
    /// Invoked on a background thread whenever another launch of the same exe asks
    /// this instance to show itself. The handler gets itself onto the UI thread.
    /// </param>
    /// <param name="onQuitRequest">
    /// Invoked on a background thread when a newer build asks this instance to stand
    /// down. The handler gets itself onto the UI thread and should exit the app.
    /// </param>
    public static LaunchResult Claim(Action onSurfaceRequest, Action onQuitRequest)
    {
        var instance = TryTakeSlot(onSurfaceRequest, onQuitRequest);
        if (instance is not null)
        {
            return new LaunchResult(LaunchOutcome.Claimed, instance, Other: null);
        }

        var other = FindOtherInstance();

        // The same exe launched twice is the habitual double-click. Surfacing it is
        // the whole point of this class.
        if (other is null || IsSameFile(other.Path, Environment.ProcessPath))
        {
            Signal(SurfaceSignalName);
            return new LaunchResult(LaunchOutcome.SurfacedExisting, Instance: null, other);
        }

#if DEBUG
        Log.Info($"A different build is running from '{other.Path}'; asking it to stand down.");
        Signal(QuitSignalName);

        instance = WaitForSlot(onSurfaceRequest, onQuitRequest);
        if (instance is not null)
        {
            return new LaunchResult(LaunchOutcome.Claimed, instance, other);
        }

        Log.Warn($"The instance running from '{other.Path}' did not stand down in time.");
#endif

        return new LaunchResult(LaunchOutcome.BlockedByOtherBuild, Instance: null, other);
    }

    /// <summary>
    /// Takes the slot and starts listening, or returns null when someone else has it.
    /// </summary>
    private static SingleInstance? TryTakeSlot(Action onSurfaceRequest, Action onQuitRequest)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

        if (!isFirst)
        {
            mutex.Dispose();
            return null;
        }

        var surfaceSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SurfaceSignalName);
        var quitSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, QuitSignalName);
        var cancellation = new CancellationTokenSource();

        var listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { surfaceSignal, quitSignal, cancellation.Token.WaitHandle };

            while (!cancellation.IsCancellationRequested)
            {
                var signalled = WaitHandle.WaitAny(handles);
                if (signalled == 2)
                {
                    return;
                }

                try
                {
                    if (signalled == 0)
                    {
                        onSurfaceRequest();
                    }
                    else
                    {
                        onQuitRequest();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("A second launch could not be handled.", ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "ImageTray single-instance listener",
        };

        listener.Start();

        return new SingleInstance(mutex, surfaceSignal, quitSignal, cancellation);
    }

    /// <summary>
    /// Waits for a replaced instance to exit, which is what frees the slot.
    /// </summary>
    private static SingleInstance? WaitForSlot(Action onSurfaceRequest, Action onQuitRequest)
    {
        var deadline = DateTime.UtcNow + HandoverTimeout;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);

            var instance = TryTakeSlot(onSurfaceRequest, onQuitRequest);
            if (instance is not null)
            {
                return instance;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds another copy of the app running for this user, whatever it was built from.
    /// </summary>
    private static RunningBuild? FindOtherInstance()
    {
        var self = Environment.ProcessId;

        foreach (var process in Process.GetProcessesByName("ImageTray"))
        {
            using (process)
            {
                if (process.Id == self)
                {
                    continue;
                }

                // A process can exit, or belong to another session, between being
                // listed and being asked about itself.
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return new RunningBuild(path, File.GetLastWriteTime(path));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not read the exe path of process {process.Id}.", ex);
                }
            }
        }

        return null;
    }

    private static bool IsSameFile(string left, string? right) =>
        !string.IsNullOrEmpty(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void Signal(string name)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(name);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is starting up or shutting down. Nothing useful
            // to do; the user can click the tray icon.
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not signal '{name}' to the running instance.", ex);
        }
    }

    public void Dispose()
    {
        _listenerCancellation.Cancel();
        _surfaceSignal.Dispose();
        _quitSignal.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned, which is fine at shutdown.
        }

        _mutex.Dispose();
        _listenerCancellation.Dispose();
    }
}
