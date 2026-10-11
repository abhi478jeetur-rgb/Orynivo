namespace Orynivo;

/// <summary>Invalidates pending queue resolution independently from playback starts.</summary>
internal sealed class QueueEditOwnership : IDisposable
{
    private long _version;
    private CancellationTokenSource? _cancellation = new();

    /// <summary>Captures ownership before asynchronous queue resolution.</summary>
    /// <returns>The queue version and its cancellation token.</returns>
    internal (long Version, CancellationToken Token) Capture() =>
        (_version, (_cancellation ?? throw new ObjectDisposedException(nameof(QueueEditOwnership))).Token);

    /// <summary>Checks whether a captured edit may publish its results.</summary>
    /// <param name="version">The captured queue version.</param>
    /// <returns>Whether the owner is open and the version remains current.</returns>
    internal bool IsCurrent(long version) => _cancellation is not null && version == _version;

    /// <summary>Cancels old resolution before a destructive queue change.</summary>
    internal void Invalidate()
    {
        if (_cancellation is null) return;
        var previous = _cancellation;
        _cancellation = new();
        ++_version;
        CancelAndDispose(previous);
    }

    /// <summary>Permanently rejects queue publication and cancels pending resolution.</summary>
    public void Dispose()
    {
        var previous = _cancellation;
        _cancellation = null;
        ++_version;
        if (previous is not null) CancelAndDispose(previous);
    }

    /// <summary>Isolates cancellation subscriber failures.</summary>
    /// <param name="cancellation">Obsolete cancellation source.</param>
    private static void CancelAndDispose(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (AggregateException) { }
        finally { cancellation.Dispose(); }
    }
}
