using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies bounded mixed-playlist requests and explicit failure/cancellation outcomes.</summary>
public sealed class PlaylistTrackBatchLoaderTests
{
    /// <summary>Duplicate IDs are queried once per source and large requests are chunked in input order.</summary>
    [Fact]
    public async Task DuplicateIds_AreChunkedPerSource()
    {
        var calls = new List<(string Source, long[] Ids)>();
        var result = await PlaylistTrackBatchLoader.LoadAsync(
            new Dictionary<string, IReadOnlyList<long>> { ["one"] = [3, 1, 3, 2, 4], ["two"] = [3, 3] },
            (source, ids, token) =>
            {
                calls.Add((source, ids.ToArray()));
                return Task.FromResult<IReadOnlyList<string>>(ids.Select(id => $"{source}:{id}").ToArray());
            }, CancellationToken.None, batchSize: 2);
        Assert.Equal(3, calls.Count);
        Assert.Equal(new long[] { 3, 1 }, calls[0].Ids);
        Assert.Equal(new long[] { 2, 4 }, calls[1].Ids);
        Assert.Equal(new long[] { 3 }, calls[2].Ids);
        Assert.Equal("two", calls[2].Source);
        Assert.Equal(LibraryLoadStatus.Complete, result.Status);
    }

    /// <summary>A failed batch does not discard earlier batches or another source's colliding ID.</summary>
    [Fact]
    public async Task BatchFailure_PreservesOtherBatchesAndSources()
    {
        var result = await PlaylistTrackBatchLoader.LoadAsync(
            new Dictionary<string, IReadOnlyList<long>> { ["one"] = [1, 2, 3], ["two"] = [1] },
            (source, ids, token) => source == "one" && ids.Contains(3)
                ? throw new InvalidOperationException("Private provider details")
                : Task.FromResult<IReadOnlyList<string>>(ids.Select(id => $"{source}:{id}").ToArray()),
            CancellationToken.None, batchSize: 2);
        Assert.Equal(new[] { "one:1", "one:2", "two:1" }, result.Rows);
        Assert.Equal(LibraryLoadStatus.Partial, result.Status);
        Assert.Equal(LibrarySourceLoadStatus.Failed, result.Sources[1].Status);
        Assert.DoesNotContain("Private", string.Join(',', result.Sources));
    }

    /// <summary>Successful missing IDs remain an empty success, distinct from failed resolution.</summary>
    [Fact]
    public async Task MissingIds_AreSuccessfulEmptyResults()
    {
        var result = await PlaylistTrackBatchLoader.LoadAsync<int>(
            new Dictionary<string, IReadOnlyList<long>> { ["one"] = [1] },
            (_, _, _) => Task.FromResult<IReadOnlyList<int>>([]), CancellationToken.None);
        Assert.Equal(LibraryLoadStatus.Complete, result.Status);
        Assert.Empty(result.Rows);
    }

    /// <summary>Global concurrency is bounded and cancellation prevents queued batches from starting.</summary>
    [Fact]
    public async Task Cancellation_StopsQueuedBatches()
    {
        using var cancellation = new CancellationTokenSource();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var task = PlaylistTrackBatchLoader.LoadAsync<int>(
            new Dictionary<string, IReadOnlyList<long>> { ["one"] = [1, 2, 3], ["two"] = [1, 2] },
            async (_, _, token) =>
            {
                if (Interlocked.Increment(ref started) == 2) admitted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return [];
            }, cancellation.Token, batchSize: 1, maximumConcurrency: 2);
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, started);
        cancellation.Cancel();
        var result = await task;
        Assert.Equal(2, started);
        Assert.Equal(LibraryLoadStatus.Cancelled, result.Status);
        Assert.All(result.Sources, source => Assert.Equal(LibrarySourceLoadStatus.Cancelled, source.Status));
    }

    /// <summary>A timed-out request is reported explicitly and a later request can recover.</summary>
    [Fact]
    public async Task Timeout_IsDistinctFromRecovery()
    {
        var sources = new Dictionary<string, IReadOnlyList<long>> { ["one"] = [1] };
        var timedOut = await PlaylistTrackBatchLoader.LoadAsync<int>(sources, async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Equal(LibrarySourceLoadStatus.TimedOut, Assert.Single(timedOut.Sources).Status);
        var recovered = await PlaylistTrackBatchLoader.LoadAsync<int>(sources,
            (_, _, _) => Task.FromResult<IReadOnlyList<int>>([1]), CancellationToken.None);
        Assert.Equal(LibraryLoadStatus.Complete, recovered.Status);
        Assert.Equal(new[] { 1 }, recovered.Rows);
    }
}
