using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks preparation completion separately from the audible lifetime.</summary>
public sealed class PlaybackReadinessTests
{
    /// <summary>Readiness returns before completion and later failure identifies its prepared player.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task PublishedPlayer_ReturnsBeforeLifetimeAndReportsLaterFailure()
    {
        var readiness = new PlaybackReadiness();
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<(IAudioPlayer, Exception)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new FakePlayer();
        var ready = readiness.Observe(lifetime.Task, (p, error) => reported.SetResult((p, error)));
        Assert.False(ready.IsCompleted);
        readiness.Publish(player);
        Assert.Same(player, await ready);
        Assert.False(lifetime.Task.IsCompleted);
        var failure = new InvalidOperationException("Synthetic failure");
        lifetime.SetException(failure);
        var result = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(player, result.Item1);
        Assert.Same(failure, result.Item2);
    }

    /// <summary>An abandoned start returns no player and does not report a playback failure.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task AbandonedPreparation_ReturnsNull()
    {
        Assert.Null(await new PlaybackReadiness().Observe(Task.CompletedTask,
            (_, _) => throw new InvalidOperationException("Must not report")));
    }

    /// <summary>Preparation failure reaches the readiness caller before any player is published.</summary>
    /// <param name="cancelled">Whether preparation failed through cancellation.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailure_Propagates(bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException() : new InvalidOperationException("Synthetic failure");
        var ready = new PlaybackReadiness().Observe(Task.FromException(failure),
            (_, _) => throw new InvalidOperationException("Must not report"));
        Assert.Same(failure, await Assert.ThrowsAsync(failure.GetType(), () => ready));
    }

    /// <summary>Supplies a player identity without accessing output hardware.</summary>
    private sealed class FakePlayer : IAudioPlayer
    {
        /// <inheritdoc/>
        public TimeSpan Duration => TimeSpan.FromSeconds(90);
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
        public void Dispose() { }
    }
}
