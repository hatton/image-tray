namespace ScreenTray.Services;

/// <summary>
/// Keeps one copy of the app running per signed-in user, and turns a second launch
/// into "show the window I already have".
/// </summary>
/// <remarks>
/// This matters more than it sounds. The app lives in the tray, so double-clicking
/// the exe out of habit is a completely reasonable way to try to get at it, and
/// without this it would start a second tray icon fighting the first over the same
/// folder.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ScreenTray.SingleInstance";
    private const string SignalName = @"Local\ScreenTray.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _signal;
    private readonly CancellationTokenSource? _listenerCancellation;

    private SingleInstance(Mutex mutex, EventWaitHandle? signal, CancellationTokenSource? listenerCancellation)
    {
        _mutex = mutex;
        _signal = signal;
        _listenerCancellation = listenerCancellation;
    }

    /// <summary>
    /// Claims the single-instance slot. Returns null when another instance already
    /// holds it, having first asked that instance to show itself.
    /// </summary>
    /// <param name="onSecondLaunch">
    /// Invoked on a background thread whenever another launch asks this instance to
    /// surface. The handler is responsible for getting onto the UI thread.
    /// </param>
    public static SingleInstance? Claim(Action onSecondLaunch)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

        if (!isFirst)
        {
            mutex.Dispose();
            SignalExistingInstance();
            return null;
        }

        var signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SignalName);
        var cancellation = new CancellationTokenSource();

        var listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { signal, cancellation.Token.WaitHandle };
            while (!cancellation.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) == 0)
                {
                    try
                    {
                        onSecondLaunch();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("A second launch could not be handled.", ex);
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "ScreenTray single-instance listener",
        };

        listener.Start();

        return new SingleInstance(mutex, signal, cancellation);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(SignalName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is starting up or shutting down. Nothing useful
            // to do; the user can click the tray icon.
        }
        catch (Exception ex)
        {
            Log.Warn("Could not ask the running instance to show itself.", ex);
        }
    }

    public void Dispose()
    {
        _listenerCancellation?.Cancel();
        _signal?.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned, which is fine at shutdown.
        }

        _mutex.Dispose();
        _listenerCancellation?.Dispose();
    }
}
