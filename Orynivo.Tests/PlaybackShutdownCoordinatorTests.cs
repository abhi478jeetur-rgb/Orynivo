using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks irreversible shutdown admission and shared drain outcomes without application UI.</summary>
public sealed class PlaybackShutdownCoordinatorTests
{
    /// <summary>Hooks observe closed admission before drain starts, and repeated requests share the result.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task Begin_ClosesAdmissionBeforeHooksAndSharesPendingDrain()
    {
        var owner = new PlaybackShutdownCoordinator();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = 0;
        var drains = 0;
        Assert.False(owner.IsShuttingDown);
        var first = owner.BeginAsync(() => { Assert.True(owner.IsShuttingDown); ++hooks; },
            () => { Assert.Equal(1, hooks); ++drains; return drain.Task; });
        var repeated = owner.BeginAsync(() => throw new Exception("Repeated hook"),
            () => throw new Exception("Repeated drain"));
        Assert.Same(first, repeated);
        Assert.False(first.IsCompleted);
        drain.SetResult();
        await first;
        Assert.True(owner.IsShuttingDown);
        Assert.Equal(1, hooks);
        Assert.Equal(1, drains);
    }

    /// <summary>Reentrant hook and drain requests cannot start another shutdown before the first returns.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task ReentrantRequests_SeeAlreadyPublishedShutdownTask()
    {
        var owner = new PlaybackShutdownCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? fromHook = null;
        Task? fromDrain = null;
        Task Reenter() => owner.BeginAsync(() => throw new Exception("Unexpected reentrant hook"),
            () => throw new Exception("Unexpected reentrant drain"));
        var first = owner.BeginAsync(() => fromHook = Reenter(),
            () => { fromDrain = Reenter(); return gate.Task; });
        Assert.Same(first, fromHook);
        Assert.Same(first, fromDrain);
        gate.SetResult();
        await first;
    }

    /// <summary>Immediate success retains permanent admission and completed task identity.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task ImmediateCompletion_RemainsSharedAndClosed()
    {
        var owner = new PlaybackShutdownCoordinator();
        var first = owner.BeginAsync(() => { }, () => Task.CompletedTask);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Same(first, owner.BeginAsync(() => throw new Exception("Repeated hook"), () => Task.CompletedTask));
        await first;
        Assert.True(owner.IsShuttingDown);
    }

    /// <summary>Synchronous hook or drain failure becomes the shared fault without reopening admission.</summary>
    /// <param name="hookFails">Whether failure occurs in the hook instead of drain admission.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousFailure_RemainsShared(bool hookFails)
    {
        var owner = new PlaybackShutdownCoordinator();
        var failure = new InvalidOperationException("Synthetic shutdown failure");
        var drainCalled = false;
        var first = owner.BeginAsync(() => { if (hookFails) throw failure; },
            () => { drainCalled = true; throw failure; });
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Equal(!hookFails, drainCalled);
        Assert.True(owner.IsShuttingDown);
        Assert.Same(first, owner.BeginAsync(() => throw new Exception("Repeated hook"), () => Task.CompletedTask));
    }

    /// <summary>Asynchronous failure is observed and retained for later shutdown callers.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task AsynchronousFailure_RemainsShared()
    {
        var owner = new PlaybackShutdownCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = owner.BeginAsync(() => { }, () => gate.Task);
        var failure = new InvalidOperationException("Synthetic drain failure");
        gate.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.True(owner.IsShuttingDown);
        Assert.Same(first, owner.BeginAsync(() => throw new Exception("Repeated hook"), () => Task.CompletedTask));
    }

    /// <summary>Cancelled drain keeps its token and never reopens shutdown admission.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task Cancellation_RemainsSharedWithItsToken()
    {
        var owner = new PlaybackShutdownCoordinator();
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = owner.BeginAsync(() => { }, () => gate.Task);
        cancellation.Cancel();
        gate.SetCanceled(cancellation.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(first.IsCanceled);
        Assert.True(owner.IsShuttingDown);
        Assert.Same(first, owner.BeginAsync(() => { }, () => Task.CompletedTask));
    }

    /// <summary>Production preparation drain keeps shutdown pending until all cancelled work releases its lease.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task ProductionDrain_WaitsForCancelledPreparationAndRunsHooksOnce()
    {
        var owner = new PlaybackShutdownCoordinator();
        var preparations = new PlaybackPreparationTracker();
        var retirements = new AudioPlayerRetirement(_ => { });
        using var lookup = preparations.Begin();
        using var seek = preparations.Begin();
        var hooks = 0;
        var first = owner.BeginAsync(() => { Assert.True(owner.IsShuttingDown); ++hooks; preparations.CancelAll(); },
            () => PlaybackOutputDrain.RetireAsync(null, true, retirements, preparations));
        Assert.True(lookup.Token.IsCancellationRequested);
        Assert.True(seek.Token.IsCancellationRequested);
        Assert.Same(first, owner.BeginAsync(() => ++hooks, () => Task.CompletedTask));
        lookup.Dispose();
        Assert.False(first.IsCompleted);
        seek.Dispose();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, hooks);
        Assert.True(owner.IsShuttingDown);
    }
}
