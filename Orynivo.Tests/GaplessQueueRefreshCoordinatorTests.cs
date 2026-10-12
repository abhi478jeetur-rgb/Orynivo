using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Exercises coalesced refresh transitions without a window, dispatcher, player, or device.</summary>
public sealed class GaplessQueueRefreshCoordinatorTests
{
    /// <summary>Ordinary output requires neither preparation nor snapshot access.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task NoGaplessOutput_ReturnsWithoutPreparation()
    {
        var fixture = new Fixture { CanRefresh = false };
        Assert.True(await fixture.Coordinator.RefreshAsync());
        Assert.Equal(0, fixture.Preparations);
    }

    /// <summary>Pending initial seek readiness is joined before checking for a new rebuild.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task ExistingPreparation_IsJoinedWithoutAnotherStart()
    {
        var fixture = new Fixture { CanRefresh = false };
        var existing = new TaskCompletionSource<bool>();
        fixture.ExistingPreparation = existing.Task;
        var first = fixture.Coordinator.RefreshAsync();
        Assert.Same(existing.Task, first);
        Assert.Same(first, fixture.Coordinator.RefreshAsync());
        Assert.False(first.IsCompleted);
        existing.SetResult(false);
        Assert.False(await first);
        Assert.Equal(0, fixture.Preparations);
    }

    /// <summary>Concurrent changes share readiness even while the old player is detached.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CurrentRefresh_IsJoinedBeforeOtherPreparationOrOutputChecks()
    {
        var fixture = new Fixture();
        var step = fixture.Enqueue();
        var first = fixture.Coordinator.RefreshAsync();
        fixture.CanRefresh = false;
        fixture.ExistingPreparation = Task.FromResult(false);
        Assert.Same(first, fixture.Coordinator.RefreshAsync());
        fixture.SnapshotCurrent = true;
        step.Ready.SetResult(true);
        Assert.True(await first);
        Assert.Equal(1, fixture.Preparations);
    }

    /// <summary>Edits arriving across multiple held preparations await the final admitted snapshot.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task DirtySnapshots_RetryAndKeepAllCallersPending()
    {
        var fixture = new Fixture();
        var firstStep = fixture.Enqueue();
        var secondStep = fixture.Enqueue();
        var finalStep = fixture.Enqueue();
        var first = fixture.Coordinator.RefreshAsync();
        Assert.Same(first, fixture.Coordinator.RefreshAsync());
        firstStep.Ready.SetResult(true);
        await secondStep.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        Assert.Same(first, fixture.Coordinator.RefreshAsync());
        secondStep.Ready.SetResult(true);
        await finalStep.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        finalStep.Ready.SetResult(true);
        Assert.True(await first);
        Assert.Equal(3, fixture.Preparations);
    }

    /// <summary>Creation that already incorporated edits avoids a redundant restart.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task UpdatedSnapshot_AvoidsRedundantRetry()
    {
        var fixture = new Fixture();
        var step = fixture.Enqueue();
        var first = fixture.Coordinator.RefreshAsync();
        Assert.Same(first, fixture.Coordinator.RefreshAsync());
        fixture.SnapshotCurrent = true;
        step.Ready.SetResult(true);
        Assert.True(await first);
        Assert.Equal(1, fixture.Preparations);
    }

    /// <summary>Stop or replacement rejects late readiness before consulting a potentially newer snapshot.</summary>
    /// <param name="ready">Whether the obsolete preparation reports a player.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededRefresh_DoesNotRetryOrReadNewSnapshot(bool ready)
    {
        var fixture = new Fixture();
        var step = fixture.Enqueue();
        var first = fixture.Coordinator.RefreshAsync();
        Assert.Same(first, fixture.Coordinator.RefreshAsync());
        ++fixture.Intent;
        fixture.RejectSnapshotRead = true;
        step.Ready.SetResult(ready);
        Assert.False(await first);
        Assert.Equal(1, fixture.Preparations);
    }

    /// <summary>Late old completion cannot clear or contaminate a newer shared refresh.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task OldCompletion_DoesNotReplaceNewPendingWork()
    {
        var fixture = new Fixture();
        var oldStep = fixture.Enqueue();
        var newStep = fixture.Enqueue();
        var old = fixture.Coordinator.RefreshAsync();
        ++fixture.Intent;
        var current = fixture.Coordinator.RefreshAsync();
        Assert.NotSame(old, current);
        oldStep.Ready.SetResult(true);
        Assert.False(await old);
        Assert.Same(current, fixture.Coordinator.RefreshAsync());
        fixture.SnapshotCurrent = true;
        newStep.Ready.SetResult(true);
        Assert.True(await current);
        Assert.Equal(2, fixture.Preparations);
    }

    /// <summary>Failures reach all waiters and release pending ownership for a later request.</summary>
    /// <param name="cancelled">Whether preparation is cancelled instead of faulted.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailure_PropagatesAndAllowsRetry(bool cancelled)
    {
        var fixture = new Fixture();
        var failed = fixture.Enqueue();
        var retryStep = fixture.Enqueue();
        var first = fixture.Coordinator.RefreshAsync();
        var joined = fixture.Coordinator.RefreshAsync();
        Assert.Same(first, joined);
        if (cancelled) failed.Ready.SetCanceled();
        else failed.Ready.SetException(new InvalidOperationException("Synthetic failure"));
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => joined);
        var retry = fixture.Coordinator.RefreshAsync();
        retryStep.Ready.SetResult(true);
        Assert.True(await retry);
        Assert.Equal(2, fixture.Preparations);
    }

    /// <summary>A synchronous adapter failure also completes callers and allows recovery.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task SynchronousPreparationFailure_AllowsRecovery()
    {
        var fixture = new Fixture { ThrowPreparing = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RefreshAsync());
        fixture.ThrowPreparing = false;
        var step = fixture.Enqueue();
        var recovered = fixture.Coordinator.RefreshAsync();
        step.Ready.SetResult(true);
        Assert.True(await recovered);
    }

    /// <summary>Installing shared work before adapter invocation also admits synchronous reentrant edits.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task ReentrantEdit_JoinsTheAdmittedRun()
    {
        var fixture = new Fixture { SnapshotCurrent = true };
        var step = fixture.Enqueue();
        Task<bool>? joined = null;
        fixture.OnPreparing = () => joined = fixture.Coordinator.RefreshAsync();
        var first = fixture.Coordinator.RefreshAsync();
        Assert.Same(first, joined);
        step.Ready.SetResult(true);
        Assert.True(await first);
        Assert.Equal(1, fixture.Preparations);
    }

    /// <summary>Immediate readiness releases the run before a subsequent independent refresh.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task SynchronousReadiness_DoesNotLeaveCompletedPendingWork()
    {
        var fixture = new Fixture();
        fixture.Enqueue().Ready.SetResult(true);
        var first = fixture.Coordinator.RefreshAsync();
        Assert.True(first.IsCompletedSuccessfully);
        fixture.Enqueue().Ready.SetResult(true);
        var second = fixture.Coordinator.RefreshAsync();
        Assert.NotSame(first, second);
        Assert.True(await second);
        Assert.Equal(2, fixture.Preparations);
    }

    /// <summary>Failure of abandoned work leaves a newer run's joining and retry ownership intact.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task OldFailure_DoesNotClearNewPendingWork()
    {
        var fixture = new Fixture { SnapshotCurrent = true };
        var oldStep = fixture.Enqueue();
        var newStep = fixture.Enqueue();
        var old = fixture.Coordinator.RefreshAsync();
        ++fixture.Intent;
        var current = fixture.Coordinator.RefreshAsync();
        oldStep.Ready.SetException(new InvalidOperationException("Synthetic obsolete failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => old);
        Assert.Same(current, fixture.Coordinator.RefreshAsync());
        newStep.Ready.SetResult(true);
        Assert.True(await current);
        Assert.Equal(2, fixture.Preparations);
    }

    /// <summary>Supplies deterministic source ownership and controlled readiness adapters.</summary>
    private sealed class Fixture
    {
        private readonly Queue<Step> _steps = new();
        /// <summary>Coordinator under test.</summary>
        internal GaplessQueueRefreshCoordinator Coordinator { get; }
        /// <summary>Current source intent, independently advanced by external transitions.</summary>
        internal long Intent;
        /// <summary>Number of preparations requested by the coordinator.</summary>
        internal int Preparations;
        /// <summary>Whether the source currently needs a refresh.</summary>
        internal bool CanRefresh = true;
        /// <summary>Whether the prepared source already has the current queue.</summary>
        internal bool SnapshotCurrent;
        /// <summary>Fails a snapshot callback that must not be reached after supersession.</summary>
        internal bool RejectSnapshotRead;
        /// <summary>Simulates failure before the adapter returns readiness.</summary>
        internal bool ThrowPreparing;
        /// <summary>Initial seek preparation to join instead of rebuilding.</summary>
        internal Task<bool>? ExistingPreparation;
        /// <summary>Optional reentrant edit during synchronous preparation admission.</summary>
        internal Action? OnPreparing;

        /// <summary>Injects adapters without constructing application state.</summary>
        internal Fixture()
        {
            Coordinator = new(() => CanRefresh, () => ExistingPreparation, Prepare,
                intent => intent == Intent,
                () => RejectSnapshotRead ? throw new InvalidOperationException("Unexpected snapshot read") : SnapshotCurrent);
        }

        /// <summary>Enqueues one held preparation.</summary>
        /// <returns>The readiness and admission gates for that preparation.</returns>
        internal Step Enqueue()
        {
            var step = new Step();
            _steps.Enqueue(step);
            return step;
        }

        /// <summary>Begins one synthetic owning start.</summary>
        /// <returns>The new intent and controllable readiness.</returns>
        private (long Intent, Task<bool> Ready) Prepare()
        {
            if (ThrowPreparing) throw new InvalidOperationException("Synthetic synchronous failure");
            ++Preparations;
            var step = _steps.Dequeue();
            var intent = ++Intent;
            OnPreparing?.Invoke();
            step.Started.SetResult();
            return (intent, step.Ready.Task);
        }
    }

    /// <summary>Controls admission and completion separately without sleeps or real playback.</summary>
    private sealed class Step
    {
        /// <summary>Completes when the adapter starts preparation.</summary>
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Supplies guarded readiness or a preparation error.</summary>
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
