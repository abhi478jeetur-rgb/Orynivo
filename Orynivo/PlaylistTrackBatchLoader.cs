namespace Orynivo;

/// <summary>Resolves distinct remote playlist identities in bounded, failure-isolated batches.</summary>
internal static class PlaylistTrackBatchLoader
{
    /// <summary>Loads provider-local IDs without losing successful batches from an unavailable source.</summary>
    /// <typeparam name="T">Metadata returned by the provider.</typeparam>
    /// <param name="sources">Opaque source keys and requested IDs, including duplicates.</param>
    /// <param name="load">Strict asynchronous provider lookup.</param>
    /// <param name="cancellationToken">Owning operation cancellation.</param>
    /// <param name="batchSize">Maximum IDs per request.</param>
    /// <param name="maximumConcurrency">Maximum concurrent requests across all sources.</param>
    /// <param name="timeout">Budget for each admitted batch.</param>
    /// <returns>Available metadata and sanitized per-batch outcomes in request order.</returns>
    internal static Task<LibraryLoadResult<T>> LoadAsync<T>(
        IReadOnlyDictionary<string, IReadOnlyList<long>> sources,
        Func<string, IReadOnlyList<long>, CancellationToken, Task<IReadOnlyList<T>>> load,
        CancellationToken cancellationToken,
        int batchSize = 200,
        int maximumConcurrency = 3,
        TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var batches = sources.SelectMany(source => source.Value.Distinct().Chunk(batchSize)
            .Select((ids, index) => (Source: source.Key, Ids: ids, Index: index))).ToArray();
        return LibrarySourceBatchLoader.LoadAsync(batches,
            batch => $"{batch.Source}:batch:{batch.Index}",
            (batch, token) => load(batch.Source, batch.Ids, token),
            cancellationToken, maximumConcurrency, timeout);
    }
}
