using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies detached-player disposal with fake players and a controllable clock, without UI or audio devices.</summary>
public sealed class AudioPlayerRetirementTests
{
    /// <summary>A timed-out transition still owns the retirement for a later complete release.</summary>
    [Fact]
    public async Task TransitionTimeout_LaterCompleteReleaseWaitsForRetiredPlayer()
    {
        var clock = new ManualClock();
        var owner = new AudioPlayerRetirement(_ => { }, clock);
        var player = new BlockingPlayer();
        using var playerCleanup = new ReleaseOnExit(player);
        var transition = owner.RetireAsync(player);
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(transition.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(clock.Timers).DueTime);
        clock.FireAll();
        await transition.WaitAsync(TimeSpan.FromSeconds(10));
        var complete = owner.RetireAsync(null, waitForCompleteDisposal: true);
        Assert.False(complete.IsCompleted);
        player.Release();
        await complete.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, player.DisposalCalls);
        await owner.RetireAsync(null, waitForCompleteDisposal: true);
    }

    /// <summary>A complete release covers both older and newly detached players.</summary>
    [Fact]
    public async Task CompleteRelease_DrainsEveryRegisteredRetirement()
    {
        var clock = new ManualClock();
        var owner = new AudioPlayerRetirement(_ => { }, clock);
        var first = new BlockingPlayer();
        using var firstCleanup = new ReleaseOnExit(first);
        var second = new BlockingPlayer();
        using var secondCleanup = new ReleaseOnExit(second);
        var transition = owner.RetireAsync(first);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.FireAll();
        await transition.WaitAsync(TimeSpan.FromSeconds(10));
        var complete = owner.RetireAsync(second, waitForCompleteDisposal: true);
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        second.Release();
        await second.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(complete.IsCompleted);
        first.Release();
        await complete.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, first.DisposalCalls);
        Assert.Equal(1, second.DisposalCalls);
    }

    /// <summary>Null ordinary transitions return immediately without waiting again for an older timeout.</summary>
    [Fact]
    public async Task NoActivePlayer_OrdinaryTransitionDoesNotAddAnotherDelay()
    {
        var clock = new ManualClock();
        var owner = new AudioPlayerRetirement(_ => { }, clock);
        var player = new BlockingPlayer();
        using var playerCleanup = new ReleaseOnExit(player);
        var transition = owner.RetireAsync(player);
        clock.FireAll();
        await transition.WaitAsync(TimeSpan.FromSeconds(10));
        await owner.RetireAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(clock.Timers);
        player.Release();
        await owner.RetireAsync(null, waitForCompleteDisposal: true).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Worker disposal leaves the caller's synchronization context responsive and cancels unused timers.</summary>
    [Fact]
    public async Task Disposal_RunsOutsideCallerContextAndCancelsBudgetTimer()
    {
        var clock = new ManualClock();
        var owner = new AudioPlayerRetirement(_ => { }, clock);
        var player = new BlockingPlayer();
        using var playerCleanup = new ReleaseOnExit(player);
        var original = SynchronizationContext.Current;
        Task transition;
        var marker = new SynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(marker);
            transition = owner.RetireAsync(player);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(marker, player.DisposalContext);
        Assert.False(transition.IsCompleted);
        player.Release();
        await transition.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(clock.Timers).IsDisposed);
    }

    /// <summary>Disposal and logging failures never fault the caller's release task.</summary>
    /// <param name="sinkThrows">Whether the supplied reporter fails as well.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalFailure_IsReportedAndIsolated(bool sinkThrows)
    {
        var reported = new List<Exception>();
        var failure = new InvalidOperationException("Synthetic disposal failure");
        var owner = new AudioPlayerRetirement(error =>
        {
            reported.Add(error);
            if (sinkThrows) throw new InvalidOperationException("Synthetic reporter failure");
        });
        var player = new BlockingPlayer(failure);
        using var playerCleanup = new ReleaseOnExit(player);
        player.Release();
        await owner.RetireAsync(player, waitForCompleteDisposal: true).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(failure, Assert.Single(reported));
        await owner.RetireAsync(null, waitForCompleteDisposal: true);
    }

    /// <summary>Cleanup failure cannot prevent other concurrently retired players from being awaited.</summary>
    [Fact]
    public async Task FailedRetirement_DoesNotShortCircuitOtherPlayers()
    {
        var owner = new AudioPlayerRetirement(_ => { });
        var failing = new BlockingPlayer(new InvalidOperationException("Synthetic failure"));
        using var failingCleanup = new ReleaseOnExit(failing);
        var blocked = new BlockingPlayer();
        using var blockedCleanup = new ReleaseOnExit(blocked);
        var first = owner.RetireAsync(failing, waitForCompleteDisposal: true);
        var complete = owner.RetireAsync(blocked, waitForCompleteDisposal: true);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        failing.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(complete.IsCompleted);
        blocked.Release();
        await complete.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A fake player blocks only its worker disposal until the test releases it.</summary>
    /// <param name="failure">Optional synthetic disposal failure.</param>
    private sealed class BlockingPlayer(Exception? failure = null) : IAudioPlayer
    {
        private readonly ManualResetEventSlim _released = new();
        /// <summary>Gets the signal that worker disposal has started.</summary>
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the signal that the disposal gate has been released.</summary>
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the number of production disposal calls.</summary>
        internal int DisposalCalls { get; private set; }
        /// <summary>Gets the synchronization context observed by disposal.</summary>
        internal SynchronizationContext? DisposalContext { get; private set; }
        /// <inheritdoc/>
        public TimeSpan Duration => TimeSpan.Zero;
        /// <inheritdoc/>
        public TimeSpan Position => TimeSpan.Zero;
        /// <inheritdoc/>
        public bool IsPaused => false;
        /// <inheritdoc/>
        public bool CanSeek => false;
        /// <inheritdoc/>
        public float Volume { get; set; }
        /// <inheritdoc/>
        public float ReplayGainFactor { get; set; }
        /// <inheritdoc/>
        public void Pause() { }
        /// <inheritdoc/>
        public void Resume() { }
        /// <inheritdoc/>
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        /// <inheritdoc/>
        public Task WaitForCompletionAsync() => Task.CompletedTask;
        /// <summary>Allows worker disposal to finish.</summary>
        internal void Release() => _released.Set();
        /// <inheritdoc/>
        public void Dispose()
        {
            DisposalCalls++;
            DisposalContext = SynchronizationContext.Current;
            Started.TrySetResult();
            _released.Wait();
            Finished.TrySetResult();
            if (failure is not null) throw failure;
        }
    }

    /// <summary>Releases a blocked fake without invoking its production disposal entry point twice.</summary>
    /// <param name="player">Fake player whose gate must be released.</param>
    private sealed class ReleaseOnExit(BlockingPlayer player) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => player.Release();
    }

    /// <summary>Controls transition budget callbacks without wall-clock timing comparisons.</summary>
    private sealed class ManualClock : TimeProvider
    {
        /// <summary>Gets the timers created for transition budgets.</summary>
        internal List<ManualTimer> Timers { get; } = [];
        /// <inheritdoc/>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }
        /// <summary>Expires every currently registered live budget.</summary>
        internal void FireAll()
        {
            foreach (var timer in Timers) timer.Fire();
        }
    }

    /// <summary>Provides an explicitly triggered one-shot timer for Task.Delay.</summary>
    /// <param name="callback">Delay completion callback.</param>
    /// <param name="state">Callback state.</param>
    /// <param name="dueTime">Requested transition budget.</param>
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        /// <summary>Gets the requested transition wait.</summary>
        internal TimeSpan DueTime { get; } = dueTime;
        /// <summary>Gets whether delay cleanup has disposed this timer.</summary>
        internal bool IsDisposed { get; private set; }
        /// <inheritdoc/>
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        /// <summary>Completes a live delay on explicit test-clock advancement.</summary>
        internal void Fire() { if (!IsDisposed) callback(state); }
        /// <inheritdoc/>
        public void Dispose() => IsDisposed = true;
        /// <inheritdoc/>
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
