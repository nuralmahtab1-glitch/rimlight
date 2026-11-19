namespace Rimlight.Shell;

/// <summary>
/// Ensures one running copy per user session. A second launch signals the first (which opens its
/// settings flyout) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Rimlight.SingleInstance";
    private const string EventName = @"Local\Rimlight.ShowSettings";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>Returns the instance guard if this is the first copy; otherwise signals the running copy and returns null.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!createdNew)
        {
            evt.Set();
            evt.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, evt);
    }

    /// <summary>Invokes <paramref name="onShow"/> (on a thread-pool thread) whenever another launch is attempted.</summary>
    public void ListenForShowRequests(Action onShow)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
