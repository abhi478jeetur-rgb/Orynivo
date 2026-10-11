namespace Orynivo;

/// <summary>Coordinates dispatcher-owned catalog loads and their single bounded complete-result cache without UI.</summary>
/// <typeparam name="T">In-memory row type; the coordinator never persists or logs row contents.</typeparam>
internal sealed class LibraryCatalogLoadCoordinator<T>
{
    private readonly LibraryViewCache<T> _cache;

    /// <summary>Creates the single cache used by this catalog load owner.</summary>
    /// <param name="maximumEntries">Positive maximum number of completed view snapshots.</param>
    internal LibraryCatalogLoadCoordinator(int maximumEntries) => _cache = new(maximumEntries);

    /// <summary>Gets the most recently admitted request version, including cache reads.</summary>
    internal int Version { get; private set; }

    /// <summary>Gets shared catalog identity also used by independent search and playlist publication guards.</summary>
    internal int Generation => _cache.Generation;

    /// <summary>Captures load ownership and catalog generation before any asynchronous source work.</summary>
    /// <param name="key">Credential-free, non-persisted view/configuration identity.</param>
    /// <param name="token">Navigation cancellation owned by the UI adapter.</param>
    /// <returns>The request used to guard result publication and deferred selection restoration.</returns>
    internal Request Begin(string key, CancellationToken token) => new(this, key, ++Version, _cache.Generation, token);

    /// <summary>Checks whether a request still owns publication independently of its row count.</summary>
    /// <param name="request">Ownership captured before loading.</param>
    /// <returns>Whether request, navigation and catalog identity remain current.</returns>
    internal bool IsCurrent(Request request) => ReferenceEquals(request.Owner, this) &&
        request.Version == Version && request.Generation == _cache.Generation && !request.Token.IsCancellationRequested;

    /// <summary>Reads a complete current snapshot only when the window's view is eligible for caching.</summary>
    /// <param name="request">Current captured view request.</param>
    /// <param name="eligible">Whether filters and view state permit unfiltered snapshot reuse.</param>
    /// <param name="result">Complete cached result when found.</param>
    /// <returns>Whether this request may reuse the cached result.</returns>
    internal bool TryGet(Request request, bool eligible, out LibraryLoadResult<T>? result)
    {
        result = null;
        return eligible && IsCurrent(request) && _cache.TryGet(request.Key, request.Generation, out result);
    }

    /// <summary>Starts independent source adapters together and combines their outcomes in supplied order.</summary>
    /// <param name="request">Captured request and navigation cancellation.</param>
    /// <param name="sources">Source adapters returning explicit sanitized outcomes.</param>
    /// <returns>Available rows/outcomes, or null when publication has been superseded or cancelled.</returns>
    internal async Task<LibraryLoadResult<T>?> LoadAsync(Request request,
        IReadOnlyList<Func<CancellationToken, Task<LibraryLoadResult<T>>>> sources)
    {
        if (!IsCurrent(request)) return null;
        var tasks = sources.Select(load => load(request.Token)).ToArray();
        var completed = await Task.WhenAll(tasks);
        if (!IsCurrent(request)) return null;
        var result = LibraryLoadResult<T>.Combine(completed);
        return result.Status == LibraryLoadStatus.Cancelled ? null : result;
    }

    /// <summary>Stores only current complete transformed results while retaining the existing cache's bound.</summary>
    /// <param name="request">Ownership captured before source loading.</param>
    /// <param name="currentKey">Current window configuration identity checked again after loading.</param>
    /// <param name="eligible">Whether current filters/view state still permit caching.</param>
    /// <param name="result">Final merged/sorted rows with original source outcomes.</param>
    /// <returns>Whether the complete result was admitted to the single cache.</returns>
    internal bool TryStore(Request request, string currentKey, bool eligible, LibraryLoadResult<T> result) =>
        eligible && IsCurrent(request) && string.Equals(request.Key, currentKey, StringComparison.Ordinal) &&
        _cache.TryStore(request.Key, request.Generation, result);

    /// <summary>Invalidates completed snapshots and every pending request's publication generation.</summary>
    internal void Invalidate() => _cache.Invalidate();

    /// <summary>Captures ownership without duplicating rows, sources, filters or UI state.</summary>
    /// <param name="Owner">Coordinator that admitted this request.</param>
    /// <param name="Key">Credential-free view/configuration cache identity.</param>
    /// <param name="Version">Admitted load version.</param>
    /// <param name="Generation">Catalog generation captured before loading.</param>
    /// <param name="Token">Captured navigation cancellation.</param>
    internal sealed record Request(LibraryCatalogLoadCoordinator<T> Owner, string Key, int Version,
        int Generation, CancellationToken Token);
}
