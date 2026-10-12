using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies whole-batch manual image ordering without a window, files or network.</summary>
public sealed class ArtistImageWriteQueueTests
{
    /// <summary>Equivalent artists wait through the entire batch while unrelated artists remain independent.</summary>
    [Fact]
    public async Task WholeBatchesStayOrderedAndReleaseIdleArtists()
    {
        var queue = new ArtistImageWriteQueue();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var first = queue.RunAsync("artist", () => true, async () =>
        {
            events.Add("first-file");
            await held.Task;
            events.Add("first-metadata-server");
            return true;
        });
        var second = queue.RunAsync("artist", () => true, () =>
        {
            events.Add("second-file-metadata-server");
            return Task.FromResult(true);
        });
        Assert.False(second.IsCompleted);
        Assert.True(await queue.RunAsync("unrelated", () => true, () => Task.FromResult(true)));
        Assert.Equal(1, queue.ActiveArtistCount);
        held.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["first-file", "first-metadata-server", "second-file-metadata-server"], events);
        Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>A superseded waiting action never writes; its newer successor still runs.</summary>
    [Fact]
    public async Task WaitingOwnershipIsRecheckedBeforeAnyWrite()
    {
        var queue = new ArtistImageWriteQueue();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RunAsync("artist", () => true, async () => { await held.Task; return true; });
        var isCurrent = true;
        var obsolete = queue.RunAsync("artist", () => isCurrent, () => throw new Exception("Obsolete write admitted"));
        isCurrent = false;
        var latest = queue.RunAsync("artist", () => true, () => Task.FromResult(true));
        Assert.False(latest.IsCompleted);
        held.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await obsolete.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await latest.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>Failed, cancelled and rejected batches release later work without propagating their outcomes to it.</summary>
    /// <param name="cancel">Whether the predecessor cancels instead of failing with an I/O error.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledBatchDoesNotPoisonSuccessors(bool cancel)
    {
        var queue = new ArtistImageWriteQueue();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RunAsync("artist", () => true, async () =>
        {
            await held.Task;
            if (cancel) throw new OperationCanceledException();
            throw new IOException("Synthetic failure");
        });
        var next = queue.RunAsync("artist", () => true, () => Task.FromResult(true));
        held.SetResult();
        var error = await Record.ExceptionAsync(() => first);
        Assert.True(cancel ? error is OperationCanceledException : error is IOException);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await queue.RunAsync("artist", () => false, () => throw new Exception("Rejected write admitted")));
        Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>Ownership-check failure releases its reservation so future actions can recover.</summary>
    [Fact]
    public async Task GuardFailureReleasesItsReservation()
    {
        var queue = new ArtistImageWriteQueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync("artist",
            () => throw new InvalidOperationException("Synthetic guard failure"), () => Task.FromResult(true)));
        Assert.True(await queue.RunAsync("artist", () => true, () => Task.FromResult(true)));
        Assert.Equal(0, queue.ActiveArtistCount);
    }
}
