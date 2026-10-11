namespace Orynivo;

/// <summary>Releases one counted loading contribution exactly once without owning controls or navigation.</summary>
internal sealed class ContentLoadingLease : IDisposable
{
    private Action? _release;

    /// <summary>Captures the release adapter for a loading contribution already acquired by the caller.</summary>
    /// <param name="release">Release action; the caller marshals UI adapters to their owning dispatcher.</param>
    internal ContentLoadingLease(Action release) => _release = release;

    /// <summary>Releases the contribution once; cancellation and completion may safely compete.</summary>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
