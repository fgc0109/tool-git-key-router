using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace GitKeyRouter.App;

public sealed class SingleInstanceGuard : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> OwnedMutexes = new(StringComparer.Ordinal);

    private readonly string? _mutexName;
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private int _listenerStarted;
    private int _disposed;

    private SingleInstanceGuard(
        string? mutexName,
        Mutex? mutex,
        EventWaitHandle? activationEvent,
        bool isPrimaryInstance)
    {
        _mutexName = mutexName;
        _mutex = mutex;
        _activationEvent = activationEvent;
        IsPrimaryInstance = isPrimaryInstance;
    }

    public bool IsPrimaryInstance { get; }

    public static SingleInstanceGuard TryAcquire(
        string? mutexName = null,
        bool signalExistingInstance = true)
    {
        var resolvedName = string.IsNullOrWhiteSpace(mutexName)
            ? CreateDefaultMutexName()
            : mutexName.Trim();
        var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            CreateActivationEventName(resolvedName));

        if (!OwnedMutexes.TryAdd(resolvedName, 0))
        {
            if (signalExistingInstance)
            {
                activationEvent.Set();
            }

            return new SingleInstanceGuard(null, null, activationEvent, false);
        }

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, resolvedName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                OwnedMutexes.TryRemove(resolvedName, out _);
                if (signalExistingInstance)
                {
                    activationEvent.Set();
                }

                return new SingleInstanceGuard(null, null, activationEvent, false);
            }

            return new SingleInstanceGuard(resolvedName, mutex, activationEvent, true);
        }
        catch
        {
            mutex?.Dispose();
            activationEvent.Dispose();
            OwnedMutexes.TryRemove(resolvedName, out _);
            throw;
        }
    }

    public void StartListening(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsPrimaryInstance || _activationEvent is null)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation requests.");
        }

        if (Interlocked.Exchange(ref _listenerStarted, 1) != 0)
        {
            throw new InvalidOperationException("The activation listener has already been started.");
        }

        _listener = Task.Run(() => ListenForActivation(activationRequested));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            _listener?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal listener shutdown path.
        }

        try
        {
            if (IsPrimaryInstance && _mutex is not null && _mutexName is not null)
            {
                _mutex.ReleaseMutex();
            }
        }
        finally
        {
            _mutex?.Dispose();
            _activationEvent?.Dispose();
            _shutdown.Dispose();
            if (_mutexName is not null)
            {
                OwnedMutexes.TryRemove(_mutexName, out _);
            }
        }
    }

    private void ListenForActivation(Action activationRequested)
    {
        var activationEvent = _activationEvent
            ?? throw new InvalidOperationException("Activation event is unavailable.");
        var waitHandles = new WaitHandle[] { activationEvent, _shutdown.Token.WaitHandle };
        while (!_shutdown.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny(waitHandles);
            if (signaled != 0 || _shutdown.IsCancellationRequested)
            {
                return;
            }

            activationRequested();
        }
    }

    private static string CreateDefaultMutexName()
    {
        var applicationDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var scope = string.IsNullOrWhiteSpace(applicationDataPath)
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : Path.GetFullPath(applicationDataPath).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        return $@"Local\GitKeyRouter.{hash[..16]}";
    }

    private static string CreateActivationEventName(string mutexName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mutexName)));
        return $@"Local\GitKeyRouter.Activate.{hash[..24]}";
    }
}
