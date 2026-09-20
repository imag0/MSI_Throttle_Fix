namespace UXTU.Headless;

public sealed class SingleWriterLease : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly bool _ownsMutex;

    private SingleWriterLease(Mutex? mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool Acquired => _ownsMutex;

    public static SingleWriterLease TryAcquire()
    {
        Mutex mutex = new(false, "Global\\MSIThrottleFix.HardwareWriter");
        try
        {
            return new SingleWriterLease(mutex, mutex.WaitOne(0, false));
        }
        catch (AbandonedMutexException)
        {
            return new SingleWriterLease(mutex, true);
        }
        catch
        {
            mutex.Dispose();
            return new SingleWriterLease(null, false);
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
    }
}
