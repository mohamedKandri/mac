namespace MacShell.Core;

// Makes sure only one MacShell runs per user session.
// A named Mutex is a lock the OS shares between processes by name: the first process creates it,
// later ones see it already exists. If MacShell crashes, Windows frees the lock automatically.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    public bool IsFirst { get; }

    public SingleInstance(string name)
    {
        // "Local\" = scoped to this sign-in session, so another user on the same PC isn't blocked.
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out bool createdNew);
        IsFirst = createdNew;
    }

    public void Dispose()
    {
        if (IsFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
