using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Exercises unique active-output handoff with production lifecycle helpers and fake audio.</summary>
public sealed class PlaybackSessionCoordinatorTests
{
    /// <summary>A linked preparation token survives lease completion and is cancelled by output detachment.</summary>
    [Fact]
    public void ReadyOutput_RetainsCancellationAfterPreparationEnds()
    {
        var starts = new PlaybackStartOwnership();
        var owner = new PlaybackSessionCoordinator(starts, new(), _ => { });
        var preparations = new PlaybackPreparationTracker();
        using var preparation = preparations.Begin();
        var intent = starts.Begin();
        Assert.True(owner.TryBegin(intent, preparation.Token, out var token));
        var player = new Player();
        Assert.True(owner.TryAttach(intent, player));
        preparation.Dispose();
        Assert.False(token.IsCancellationRequested);
        Assert.Same(player, owner.Detach());
        Assert.True(token.IsCancellationRequested);
        Assert.Null(owner.Player);
        Assert.Equal(CancellationToken.None, owner.Token);
        Assert.Null(owner.Detach());
    }

    /// <summary>Old seek completion cannot detach or attach over a replacement, which retains its token.</summary>
    [Fact]
    public void SupersededSeek_CannotMutateReplacementOutput()
    {
        var starts = new PlaybackStartOwnership();
        var owner = new PlaybackSessionCoordinator(starts, new(), _ => { });
        var oldIntent = starts.Begin();
        Assert.True(owner.TryBegin(oldIntent, default, out var oldToken));
        var oldPlayer = new Player();
        Assert.True(owner.TryAttach(oldIntent, oldPlayer));
        var newIntent = starts.Begin();
        Assert.Same(oldPlayer, owner.Detach());
        Assert.True(owner.TryBegin(newIntent, default, out var newToken));
        var replacement = new Player();
        Assert.True(owner.TryAttach(newIntent, replacement));
        Assert.False(owner.TryAttach(oldIntent, oldPlayer));
        Assert.Null(owner.DetachForRetry(oldIntent, oldPlayer));
        Assert.Null(owner.DetachForRetry(newIntent, oldPlayer));
        Assert.Same(replacement, owner.Player);
        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(newToken.IsCancellationRequested);
        owner.Detach();
    }

    /// <summary>Gapless reconciliation preserves its session token while transferring each obsolete output once.</summary>
    [Fact]
    public void GaplessRetry_KeepsTokenAndUniquePlayerOwnership()
    {
        var starts = new PlaybackStartOwnership();
        var owner = new PlaybackSessionCoordinator(starts, new(), _ => { });
        var intent = starts.Begin();
        Assert.True(owner.TryBegin(intent, default, out var token));
        var first = new Player();
        Assert.True(owner.TryAttach(intent, first));
        Assert.False(owner.TryAttach(intent, new Player()));
        Assert.Same(first, owner.DetachForRetry(intent, first));
        Assert.Null(owner.DetachForRetry(intent, first));
        Assert.Equal(token, owner.Token);
        Assert.False(token.IsCancellationRequested);
        var final = new Player();
        Assert.True(owner.TryAttach(intent, final));
        Assert.Same(final, owner.Detach());
    }

    /// <summary>Cancelled, obsolete, occupied and shutdown sessions reject new output admission.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task Admission_RejectsInvalidStartsAndShutdownFactoryResult()
    {
        var starts = new PlaybackStartOwnership();
        var shutdown = new PlaybackShutdownCoordinator();
        var owner = new PlaybackSessionCoordinator(starts, shutdown, _ => { });
        var intent = starts.Begin();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(owner.TryBegin(intent, cancelled.Token, out _));
        Assert.False(owner.TryBegin(intent - 1, default, out _));
        Assert.True(owner.TryBegin(intent, default, out _));
        Assert.False(owner.TryBegin(intent, default, out _));
        await shutdown.BeginAsync(() => { }, () => Task.CompletedTask);
        Assert.False(owner.TryAttach(intent, new Player()));
        owner.Detach();
        Assert.False(owner.TryBegin(starts.Begin(), default, out _));
    }

    /// <summary>Preparation cancellation prevents a late factory from acquiring output even with a current intent.</summary>
    [Fact]
    public void CancelledPreparation_RejectsLateAttachment()
    {
        var starts = new PlaybackStartOwnership();
        var owner = new PlaybackSessionCoordinator(starts, new(), _ => { });
        using var preparation = new CancellationTokenSource();
        var intent = starts.Begin();
        Assert.True(owner.TryBegin(intent, preparation.Token, out _));
        preparation.Cancel();
        Assert.False(owner.TryAttach(intent, new Player()));
        Assert.Null(owner.Detach());
    }

    /// <summary>Reentrant cancellation sees detached ownership and cannot have its new output cleared by the old stop.</summary>
    [Fact]
    public void CancellationCallback_CanInstallANewSessionWithoutLosingOwnership()
    {
        var starts = new PlaybackStartOwnership();
        var owner = new PlaybackSessionCoordinator(starts, new(), _ => { });
        var firstIntent = starts.Begin();
        Assert.True(owner.TryBegin(firstIntent, default, out var token));
        var first = new Player();
        var next = new Player();
        Assert.True(owner.TryAttach(firstIntent, first));
        using var registration = token.Register(() =>
        {
            Assert.Null(owner.Player);
            Assert.Equal(CancellationToken.None, owner.Token);
            var intent = starts.Begin();
            Assert.True(owner.TryBegin(intent, default, out _));
            Assert.True(owner.TryAttach(intent, next));
        });
        Assert.Same(first, owner.Detach());
        Assert.Same(next, owner.Player);
        Assert.False(owner.Token.IsCancellationRequested);
        owner.Detach();
    }

    /// <summary>Throwing cancellation and diagnostic callbacks cannot prevent unique output retirement.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task FailingCancellation_StillTransfersPlayerToCompleteDrain()
    {
        var starts = new PlaybackStartOwnership();
        var reports = 0;
        var owner = new PlaybackSessionCoordinator(starts, new(), _ => { ++reports; throw new Exception("Synthetic sink"); });
        Assert.True(owner.TryBegin(starts.Begin(), default, out var token));
        var player = new Player();
        Assert.True(owner.TryAttach(starts.Current, player));
        using var registration = token.Register(() => throw new Exception("Synthetic callback"));
        var detached = owner.Detach();
        Assert.Same(player, detached);
        Assert.Equal(1, reports);
        await PlaybackOutputDrain.RetireAsync(detached, true, new(_ => { }), new());
        Assert.Equal(1, player.Disposals);
        Assert.Null(owner.Detach());
    }

    /// <summary>Minimal audio output exposing unique disposal without opening an audio device.</summary>
    private sealed class Player : IAudioPlayer
    {
        /// <summary>Counts retirement calls.</summary>
        internal int Disposals;
        /// <inheritdoc/>
        public TimeSpan Duration => TimeSpan.Zero;
        /// <inheritdoc/>
        public TimeSpan Position => TimeSpan.Zero;
        /// <inheritdoc/>
        public bool IsPaused => false;
        /// <inheritdoc/>
        public bool CanSeek => true;
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
        /// <inheritdoc/>
        public void Dispose() => ++Disposals;
    }
}
