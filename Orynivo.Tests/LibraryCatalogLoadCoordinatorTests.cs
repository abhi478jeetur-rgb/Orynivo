using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks catalog orchestration, publication ownership and the single bounded cache without a window.</summary>
public sealed class LibraryCatalogLoadCoordinatorTests
{
    /// <summary>Sources begin together, receive navigation cancellation and combine in configured order.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task IndependentSources_CompleteOutOfOrderButRetainConfiguredOrder()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        using var cancellation = new CancellationTokenSource();
        var request = owner.Begin("Tracks", cancellation.Token);
        var first = Gate();
        var second = Gate();
        var admitted = 0;
        var load = owner.LoadAsync(request,
        [
            token => { Assert.Equal(cancellation.Token, token); ++admitted; return first.Task; },
            token => { Assert.Equal(cancellation.Token, token); ++admitted; return second.Task; }
        ]);
        Assert.Equal(2, admitted);
        second.SetResult(Success("remote", [2]));
        Assert.False(load.IsCompleted);
        first.SetResult(Success("local", [1]));
        var result = await load;
        Assert.Equal([1, 2], result!.Rows);
        Assert.Equal(["local", "remote"], result.Sources.Select(source => source.SourceKey));
    }

    /// <summary>Old completion cannot publish or overwrite the same view's newer cache entry.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task OverlappingRequests_RejectLateCompletionAndCacheWrite()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        var old = owner.Begin("Tracks", default);
        var gate = Gate();
        var pending = owner.LoadAsync(old, [_ => gate.Task]);
        var current = owner.Begin("Tracks", default);
        var replacement = Success("local", [7]);
        Assert.True(owner.TryStore(current, "Tracks", true, replacement));
        gate.SetResult(Success("local", [1]));
        Assert.Null(await pending);
        Assert.False(owner.TryStore(old, "Tracks", true, Success("local", [1])));
        Assert.False(owner.TryGet(old, true, out _));
        Assert.True(owner.TryGet(current, true, out var cached));
        Assert.Same(replacement, cached);
    }

    /// <summary>Catalog/profile/settings invalidation rejects in-flight publication and permits a fresh recovery.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task Invalidation_RejectsPendingResultAndClearsSnapshots()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        var request = owner.Begin("Artists", default);
        Assert.True(owner.TryStore(request, "Artists", true, Success("local", [1])));
        var gate = Gate();
        var pending = owner.LoadAsync(request, [_ => gate.Task]);
        owner.Invalidate();
        gate.SetResult(Success("local", [2]));
        Assert.Null(await pending);
        Assert.False(owner.IsCurrent(request));
        var recovery = owner.Begin("Artists", default);
        Assert.False(owner.TryGet(recovery, true, out _));
        Assert.True(owner.TryStore(recovery, "Artists", true, Success("local", [3])));
    }

    /// <summary>Navigation cancellation rejects a backend result even when its adapter ignores cancellation.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task Cancellation_RejectsLateSuccessfulSource()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        using var cancellation = new CancellationTokenSource();
        var request = owner.Begin("Albums", cancellation.Token);
        var gate = Gate();
        var pending = owner.LoadAsync(request, [_ => gate.Task]);
        cancellation.Cancel();
        gate.SetResult(Success("local", [1]));
        Assert.Null(await pending);
        Assert.False(owner.TryStore(request, "Albums", true, Success("local", [1])));
    }

    /// <summary>Partial and failed outcomes remain publishable but only complete recovery enters the cache.</summary>
    /// <param name="localSucceeds">Whether local rows remain available during remote failure.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSources_RemainExplicitAndDoNotPoisonRecovery(bool localSucceeds)
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        var request = owner.Begin("Tracks", default);
        var result = await owner.LoadAsync(request,
        [
            token => LibrarySourceLoader.LoadAsync<int>("local", _ => localSucceeds
                ? Task.FromResult<IReadOnlyList<int>>([1]) : throw new IOException("Synthetic private detail"), token),
            token => LibrarySourceLoader.LoadAsync<int>("remote", _ => throw new IOException("Synthetic private detail"), token)
        ]);
        Assert.Equal(localSucceeds ? LibraryLoadStatus.Partial : LibraryLoadStatus.Failed, result!.Status);
        Assert.False(owner.TryStore(request, "Tracks", true, result));
        Assert.False(owner.TryGet(request, true, out _));
        var recovery = owner.Begin("Tracks", default);
        Assert.True(owner.TryStore(recovery, "Tracks", true, Success("local", [])));
        Assert.True(owner.TryGet(recovery, true, out var empty));
        Assert.Empty(empty!.Rows);
    }

    /// <summary>Changed configuration or filter eligibility prevents transformed rows entering the wrong cache.</summary>
    [Fact]
    public void ConfigurationAndFilters_RejectCacheReuseAndPublicationStorage()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        var request = owner.Begin("Tracks;source-a", default);
        var result = Success("local", [3, 2, 1]);
        Assert.False(owner.TryStore(request, "Tracks;source-b", true, result));
        Assert.False(owner.TryStore(request, request.Key, false, result));
        Assert.True(owner.TryStore(request, request.Key, true, result));
        Assert.False(owner.TryGet(request, false, out _));
        Assert.True(owner.TryGet(request, true, out var cached));
        Assert.Same(result, cached);
    }

    /// <summary>Older, cancelled or foreign requests do not invoke source adapters.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task InvalidAdmission_DoesNotStartSourceWork()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        var old = owner.Begin("Tracks", default);
        owner.Begin("Albums", default);
        Assert.Null(await owner.LoadAsync(old, [_ => throw new Exception("Unexpected source") ]));
        var foreign = new LibraryCatalogLoadCoordinator<int>(3).Begin("Albums", default);
        Assert.False(owner.IsCurrent(foreign));
        Assert.Null(await owner.LoadAsync(foreign, [_ => throw new Exception("Unexpected source") ]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = owner.Begin("Tracks", cancellation.Token);
        Assert.Null(await owner.LoadAsync(cancelled, [_ => throw new Exception("Unexpected source") ]));
    }

    /// <summary>Explicit source cancellation prevents publication even without navigation-token cancellation.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CancelledOutcome_IsNotPublished()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(3);
        var request = owner.Begin("Tracks", default);
        Assert.Null(await owner.LoadAsync(request,
            [_ => Task.FromResult(new LibraryLoadResult<int>([], [new("local", LibrarySourceLoadStatus.Cancelled)]))]));
    }

    /// <summary>The coordinator retains the original LRU bound and reading a view advances request ownership.</summary>
    [Fact]
    public void CacheRead_AdvancesOwnershipAndPreservesLruBound()
    {
        var owner = new LibraryCatalogLoadCoordinator<int>(2);
        foreach (var key in new[] { "Artists", "Albums" })
        {
            var request = owner.Begin(key, default);
            Assert.True(owner.TryStore(request, key, true, Success("local", [1])));
        }
        var artists = owner.Begin("Artists", default);
        Assert.True(owner.TryGet(artists, true, out _));
        var tracks = owner.Begin("Tracks", default);
        Assert.False(owner.IsCurrent(artists));
        Assert.True(owner.TryStore(tracks, "Tracks", true, Success("local", [2])));
        Assert.False(owner.TryGet(owner.Begin("Albums", default), true, out _));
        Assert.True(owner.TryGet(owner.Begin("Artists", default), true, out _));
    }

    /// <summary>Creates a successful typed source result without catalog I/O.</summary>
    /// <param name="source">Opaque source identity.</param>
    /// <param name="rows">Synthetic rows.</param>
    /// <returns>The successful result.</returns>
    private static LibraryLoadResult<int> Success(string source, IReadOnlyList<int> rows) =>
        new(rows, [new(source, LibrarySourceLoadStatus.Success)]);

    /// <summary>Creates an independently controlled source completion.</summary>
    /// <returns>The held source result.</returns>
    private static TaskCompletionSource<LibraryLoadResult<int>> Gate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
