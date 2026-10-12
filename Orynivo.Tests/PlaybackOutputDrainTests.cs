using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks complete output release using the production trackers without a window or audio device.</summary>
public sealed class PlaybackOutputDrainTests
{
    /// <summary>An empty ordinary transition does not wait for unrelated lookup or old retirement.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task OrdinaryEmptyTransition_DoesNotDrainPendingWork()
    {
        var preparations = new PlaybackPreparationTracker();
        using var lookup = preparations.Begin();
        var retirements = new AudioPlayerRetirement(_ => { }, transitionBudget: TimeSpan.Zero);
        var older = new HeldPlayer();
        try
        {
            await retirements.RetireAsync(older);
            await PlaybackOutputDrain.RetireAsync(null, false, retirements, preparations);
            Assert.False(lookup.Completion.IsCompleted);
            Assert.False(lookup.Token.IsCancellationRequested);
            Assert.False(retirements.RetireAsync(null, true).IsCompleted);
        }
        finally { older.Release.Set(); }
        await retirements.RetireAsync(null, true).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Ordinary transition retains its retirement budget without awaiting preparation.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task OrdinaryPlayerTransition_DoesNotAwaitPreparationOrFullDisposal()
    {
        var preparations = new PlaybackPreparationTracker();
        using var lookup = preparations.Begin();
        var retirements = new AudioPlayerRetirement(_ => { }, transitionBudget: TimeSpan.Zero);
        var player = new HeldPlayer();
        try
        {
            await PlaybackOutputDrain.RetireAsync(player, false, retirements, preparations);
            await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(lookup.Completion.IsCompleted);
            Assert.Equal(1, player.Disposals);
        }
        finally { player.Release.Set(); }
        await retirements.RetireAsync(null, true).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Complete release waits for every cancelled lookup and seek even without active output.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task FullReleaseWithoutPlayer_WaitsForAllCapturedPreparations()
    {
        var preparations = new PlaybackPreparationTracker();
        using var lookup = preparations.Begin();
        using var seek = preparations.Begin();
        var retirements = new AudioPlayerRetirement(_ => { });
        preparations.CancelAll();
        var release = PlaybackOutputDrain.RetireAsync(null, true, retirements, preparations);
        Assert.True(lookup.Token.IsCancellationRequested);
        Assert.True(seek.Token.IsCancellationRequested);
        lookup.Dispose();
        Assert.False(release.IsCompleted);
        seek.Dispose();
        await release.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Current disposal starts before pending lookup resolves, and older disposal is included.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task FullRelease_StartsCurrentRetirementAndIncludesOlderWork()
    {
        var preparations = new PlaybackPreparationTracker();
        using var lookup = preparations.Begin();
        var retirements = new AudioPlayerRetirement(_ => { }, transitionBudget: TimeSpan.Zero);
        var older = new HeldPlayer();
        var current = new HeldPlayer();
        try
        {
            await retirements.RetireAsync(older);
            preparations.CancelAll();
            var release = PlaybackOutputDrain.RetireAsync(current, true, retirements, preparations);
            await current.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(lookup.Completion.IsCompleted);
            current.Release.Set();
            lookup.Dispose();
            Assert.False(release.IsCompleted);
            older.Release.Set();
            await release.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, current.Disposals);
            Assert.Equal(1, older.Disposals);
        }
        finally { older.Release.Set(); current.Release.Set(); }
    }

    /// <summary>A cancelled factory's late output cannot escape the second retirement pass.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task LateFactory_FullReleaseWaitsForUniqueLateDisposal()
    {
        var preparations = new PlaybackPreparationTracker();
        var starts = new PlaybackStartOwnership();
        var retirements = new AudioPlayerRetirement(_ => { }, transitionBudget: TimeSpan.Zero);
        var factory = new TaskCompletionSource<(IAudioPlayer, AudioFileInfo)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new HeldPlayer();
        CancellationToken factoryToken = default;
        IAudioPlayer? published = null;
        var pending = starts.RunAsync(async intent =>
        {
            using var preparation = preparations.Begin();
            var created = await starts.CreateAsync(intent, token => { factoryToken = token; return factory.Task; }, preparation.Token, retirements);
            published = created?.Player;
        });
        try
        {
            starts.Invalidate();
            preparations.CancelAll();
            var release = PlaybackOutputDrain.RetireAsync(null, true, retirements, preparations);
            Assert.True(factoryToken.IsCancellationRequested);
            Assert.False(release.IsCompleted);
            factory.SetResult((player, new("flac", 44100, 2, 44100, false, "flac", TimeSpan.FromSeconds(90))));
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(published);
            await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(release.IsCompleted);
            player.Release.Set();
            await release.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, player.Disposals);
        }
        finally { player.Release.Set(); }
    }

    /// <summary>Overlapping complete waits do not transfer an already detached player twice.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task RepeatedRelease_DrainsSharedWorkWithoutDuplicateDisposal()
    {
        var preparations = new PlaybackPreparationTracker();
        using var seek = preparations.Begin();
        var retirements = new AudioPlayerRetirement(_ => { });
        var player = new HeldPlayer();
        try
        {
            var first = PlaybackOutputDrain.RetireAsync(player, true, retirements, preparations);
            var second = PlaybackOutputDrain.RetireAsync(null, true, retirements, preparations);
            await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            seek.Dispose();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            player.Release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, player.Disposals);
        }
        finally { player.Release.Set(); }
    }

    /// <summary>Disposal and reporter failures do not bypass pending preparation or fault release.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task DisposalFailure_DoesNotSkipPreparationWait()
    {
        var preparations = new PlaybackPreparationTracker();
        using var lookup = preparations.Begin();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirements = new AudioPlayerRetirement(_ => { reported.TrySetResult(); throw new InvalidOperationException("Synthetic reporter failure"); });
        var player = new HeldPlayer { FailDisposal = true };
        player.Release.Set();
        var release = PlaybackOutputDrain.RetireAsync(player, true, retirements, preparations);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(release.IsCompleted);
        lookup.Dispose();
        await release.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, player.Disposals);
    }

    /// <summary>A fake player whose disposal blocks only the production retirement worker.</summary>
    private sealed class HeldPlayer : IAudioPlayer
    {
        /// <summary>Signals worker disposal admission.</summary>
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Allows worker disposal to finish.</summary>
        internal readonly ManualResetEventSlim Release = new();
        /// <summary>Counts production disposal calls.</summary>
        internal int Disposals;
        /// <summary>Simulates a disposal failure after the gate opens.</summary>
        internal bool FailDisposal;
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
        /// <inheritdoc/>
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic disposal gate timed out");
            if (FailDisposal) throw new InvalidOperationException("Synthetic disposal failure");
        }
    }
}
