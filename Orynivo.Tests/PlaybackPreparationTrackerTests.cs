using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects pending creation drains without waiting for whole playback lifetimes or opening devices.</summary>
public sealed class PlaybackPreparationTrackerTests
{
    /// <summary>Cancellation retains every pending preparation until its own cleanup finishes.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CancelAll_DrainWaitsForEveryPreparation()
    {
        var tracker = new PlaybackPreparationTracker();
        using var first = tracker.Begin();
        using var second = tracker.Begin();
        var firstToken = first.Token;
        var secondToken = second.Token;
        tracker.CancelAll();
        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(secondToken.IsCancellationRequested);
        var drain = tracker.DrainAsync();
        first.Dispose();
        Assert.False(drain.IsCompleted);
        second.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(tracker.DrainAsync().IsCompletedSuccessfully);
    }

    /// <summary>Publishing a prepared player ends preparation without cancelling its playback token.</summary>
    [Fact]
    public void PublishedPreparation_DoesNotCancelLinkedPlayback()
    {
        var tracker = new PlaybackPreparationTracker();
        using var preparation = tracker.Begin();
        using var playback = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token);
        preparation.Dispose();
        preparation.Dispose();
        tracker.CancelAll();
        Assert.False(playback.IsCancellationRequested);
        Assert.True(tracker.DrainAsync().IsCompletedSuccessfully);
    }

    /// <summary>A failing cancellation callback cannot leave another preparation uncancelled.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CallbackFailure_DoesNotShortCircuitCancellationOrDrain()
    {
        var tracker = new PlaybackPreparationTracker();
        using var first = tracker.Begin();
        using var second = tracker.Begin();
        using var callback = first.Token.Register(() => throw new InvalidOperationException("Synthetic callback"));
        tracker.CancelAll();
        Assert.True(second.Token.IsCancellationRequested);
        var drain = tracker.DrainAsync();
        Assert.False(drain.IsCompleted);
        first.Dispose();
        second.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A factory ignoring cancellation remains tracked until its late player transfers to retirement.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task LateFactory_RequiresPreparationThenRetirementDrain()
    {
        var tracker = new PlaybackPreparationTracker();
        var starts = new PlaybackStartOwnership();
        var retirement = new AudioPlayerRetirement(_ => { }, transitionBudget: TimeSpan.Zero);
        var factory = new TaskCompletionSource<(IAudioPlayer Player, AudioFileInfo Info)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new BlockingPlayer();
        try
        {
            var pending = starts.RunAsync(async intent =>
            {
                using var preparation = tracker.Begin();
                await starts.CreateAsync(intent, _ => factory.Task, preparation.Token, retirement);
            });
            starts.Invalidate();
            tracker.CancelAll();
            var drain = tracker.DrainAsync();
            Assert.False(drain.IsCompleted);
            factory.SetResult((player, new("flac", 44100, 2, 44100, false, "flac", TimeSpan.FromSeconds(90))));
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var release = retirement.RetireAsync(null, waitForCompleteDisposal: true);
            Assert.False(release.IsCompleted);
            player.Release.Set();
            await release.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, player.Disposals);
        }
        finally { player.Release.Set(); }
    }

    /// <summary>A fake output holding disposal behind an explicit gate.</summary>
    private sealed class BlockingPlayer : IAudioPlayer
    {
        /// <summary>Signals disposal entering its worker.</summary>
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Allows disposal to complete.</summary>
        internal readonly ManualResetEventSlim Release = new();
        /// <summary>Counts unique transfers to retirement.</summary>
        internal int Disposals;
        /// <inheritdoc/>
        public TimeSpan Duration => TimeSpan.FromSeconds(90);
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
        /// <inheritdoc/>
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            Started.TrySetResult();
            Release.Wait();
        }
    }
}
