using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Exercises overlapping factories and obsolete errors without native output or a window.</summary>
public sealed class PlaybackStartOwnershipTests
{
    /// <summary>A factory ignoring cancellation cannot publish its late player over the newer result.</summary>
    /// <param name="stop">Whether an explicit stop supersedes the factory instead of another start.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateFactory_IsRetiredExactlyOnce(bool stop)
    {
        var owner = new PlaybackStartOwnership();
        var retirement = new AudioPlayerRetirement(_ => { });
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var factory = new TaskCompletionSource<(IAudioPlayer Player, AudioFileInfo Info)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new FakePlayer();
        var request = owner.Begin();
        var pending = owner.CreateAsync(request, captured =>
        {
            Assert.Equal(token, captured);
            return factory.Task;
        }, token, retirement);
        cancellation.Cancel();
        long current;
        if (stop) { owner.Invalidate(); current = request; }
        else current = owner.Begin();
        var newer = new FakePlayer();
        if (!stop)
        {
            var accepted = await owner.CreateAsync(current,
                _ => Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((newer, Info)),
                CancellationToken.None, retirement);
            Assert.Same(newer, accepted!.Value.Player);
        }
        factory.SetResult((old, Info));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await retirement.RetireAsync(null, waitForCompleteDisposal: true);
        Assert.Equal(1, old.Disposals);
        Assert.Equal(0, newer.Disposals);
        Assert.True(token.IsCancellationRequested);
    }

    /// <summary>Invalidated requests never enter a backend factory.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task InvalidatedIntent_DoesNotCreatePlayer()
    {
        var owner = new PlaybackStartOwnership();
        var request = owner.Begin();
        owner.Invalidate();
        Assert.Null(await owner.CreateAsync(request,
            _ => throw new InvalidOperationException("Must not create"),
            CancellationToken.None, new AudioPlayerRetirement(_ => { })));
    }

    /// <summary>Only obsolete failures are hidden from existing caller stop handlers.</summary>
    /// <param name="obsolete">Whether another start supersedes the failing operation.</param>
    /// <param name="cancelled">Whether the synthetic failure represents cancellation.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failure_PropagatesOnlyForCurrentIntent(bool obsolete, bool cancelled)
    {
        var owner = new PlaybackStartOwnership();
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = owner.RunAsync(_ => operation.Task);
        if (obsolete) owner.Begin();
        Exception error = cancelled ? new OperationCanceledException() : new InvalidOperationException("Synthetic failure");
        operation.SetException(error);
        if (obsolete) await pending.WaitAsync(TimeSpan.FromSeconds(10));
        else Assert.Same(error, await Assert.ThrowsAsync(error.GetType(), () => pending));
    }

    /// <summary>Provides synthetic PCM format metadata.</summary>
    private static AudioFileInfo Info => new("flac", 44100, 2, 44100, false, "flac", TimeSpan.FromSeconds(90));

    /// <summary>Counts disposal without accessing a device.</summary>
    private sealed class FakePlayer : IAudioPlayer
    {
        /// <summary>Counts transferred player disposals.</summary>
        internal int Disposals;
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
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
