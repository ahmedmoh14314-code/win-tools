namespace WinTools.Windows.Settings;

/// <summary>
/// "The settings file changed": a named auto-reset event in the session's Local\ namespace. The settings window sets it
/// after a save; the resident host waits on it on the thread pool (no polling) and reloads the file. It carries no data,
/// so anything able to set it (only processes of the same user session) can at most make the host re-read its own file.
/// </summary>
public static class SettingsSignal
{
    public const string Name = @"Local\WinTools.SettingsChanged";

    /// <summary>Tells a running host to reload. Does nothing visible when no host is running.</summary>
    public static void Raise()
    {
        using var e = new EventWaitHandle(false, EventResetMode.AutoReset, Name);
        e.Set();
    }

    /// <summary>Calls <paramref name="onChanged"/> on a pool thread each time the signal is raised, until disposed.</summary>
    public static IDisposable Listen(Action onChanged)
    {
        var e = new EventWaitHandle(false, EventResetMode.AutoReset, Name);
        var registration = ThreadPool.RegisterWaitForSingleObject(e, (_, _) => onChanged(), null, Timeout.Infinite, executeOnlyOnce: false);
        return new Listener(e, registration);
    }

    private sealed class Listener(EventWaitHandle e, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            e.Dispose();
        }
    }
}
