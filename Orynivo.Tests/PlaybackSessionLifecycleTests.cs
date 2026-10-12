using Orynivo.Audio;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Exercises factory, seek, readiness and complete release with production lifecycle helpers without UI.</summary>
public sealed class PlaybackSessionLifecycleTests
{
    /// <summary>Readiness waits for factory and seek, applies pause and ends preparation before audible completion.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task Readiness_WaitsForHeldFactoryAndSeekButNotAudibleLifetime()
    {
        var fixture = new Lifecycle();
        var factory = new TaskCompletionSource<(IAudioPlayer, AudioFileInfo)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new Output();
        var ready = fixture.Start(factory.Task, TimeSpan.FromSeconds(23), true);
        Assert.False(ready.IsCompleted);
        Assert.Null(fixture.Session.Player);
        factory.SetResult((player, Info));
        await player.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(player, fixture.Session.Player);
        Assert.False(ready.IsCompleted);
        Assert.False(player.IsPaused);
        player.SeekRelease.SetResult();
        Assert.Same(player, await ready.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(player.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(23), player.Position);
        Assert.True(fixture.Preparations.DrainAsync().IsCompletedSuccessfully);
        Assert.False(player.Completion.Task.IsCompleted);
        Assert.False(fixture.Lifetime!.IsCompleted);
        player.DisposeRelease.Set();
        await fixture.StopAsync();
    }

    /// <summary>A late cancelled factory result is retired once and complete release waits for its disposal.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CompleteRelease_DrainsLateFactoryAndItsUniqueRetirement()
    {
        var fixture = new Lifecycle();
        var factory = new TaskCompletionSource<(IAudioPlayer, AudioFileInfo)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = fixture.Start(factory.Task, default, false);
        var drain = fixture.StopAsync();
        Assert.False(drain.IsCompleted);
        var late = new Output();
        factory.SetResult((late, Info));
        await late.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(drain.IsCompleted);
        Assert.Null(fixture.Session.Player);
        late.DisposeRelease.Set();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await ready);
        Assert.Equal(1, late.Disposals);
        Assert.False(late.SeekEntered.Task.IsCompleted);
        await fixture.StopAsync();
        Assert.Equal(1, late.Disposals);
    }

    /// <summary>Complete release waits for held seek even after detached player disposal has finished.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CompleteRelease_DrainsSeekAfterPlayerDisposal()
    {
        var fixture = new Lifecycle();
        var player = new Output();
        var ready = fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((player, Info)), TimeSpan.FromSeconds(12), true);
        await player.SeekEntered.Task;
        var drain = fixture.StopAsync();
        await player.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        player.DisposeRelease.Set();
        await player.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(drain.IsCompleted);
        player.SeekRelease.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await ready);
        Assert.False(player.IsPaused);
        Assert.Equal(1, player.Disposals);
    }

    /// <summary>A superseded seek or seek failure cannot publish readiness, pause or stop replacement output.</summary>
    /// <param name="failSeek">Whether the obsolete backend seek faults rather than succeeds.</param>
    /// <returns>The asynchronous regression test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededSeek_PreservesNewReadyOutput(bool failSeek)
    {
        var fixture = new Lifecycle();
        var old = new Output();
        var oldReady = fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((old, Info)), TimeSpan.FromSeconds(17), true);
        await old.SeekEntered.Task;
        old.DisposeRelease.Set();
        await fixture.StopAsync(full: false);
        var current = new Output();
        current.SeekRelease.SetResult();
        var currentReady = fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((current, Info)), TimeSpan.FromSeconds(31), true);
        Assert.Same(current, await currentReady);
        if (failSeek) old.SeekRelease.SetException(new InvalidOperationException("Synthetic obsolete seek"));
        else old.SeekRelease.SetResult();
        Assert.Null(await oldReady.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(current, fixture.Session.Player);
        Assert.True(current.IsPaused);
        Assert.False(old.IsPaused);
        Assert.False(fixture.Session.Token.IsCancellationRequested);
        current.DisposeRelease.Set();
        await fixture.StopAsync();
    }

    /// <summary>Current seek failure remains best effort and publishes the same owned output.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CurrentSeekFailure_DoesNotAbandonPlayback()
    {
        var fixture = new Lifecycle();
        var player = new Output();
        player.SeekRelease.SetException(new InvalidOperationException("Synthetic resume failure"));
        Assert.Same(player, await fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((player, Info)), TimeSpan.FromSeconds(5), true));
        Assert.True(player.IsPaused);
        player.DisposeRelease.Set();
        await fixture.StopAsync();
    }

    /// <summary>Reentrant backend pause cannot publish obsolete readiness after transferring output ownership.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task ReentrantPause_RejectsObsoleteReadiness()
    {
        var fixture = new Lifecycle();
        var player = new Output();
        player.SeekRelease.SetResult();
        player.DisposeRelease.Set();
        Task? drain = null;
        player.OnPause = () => drain = fixture.StopAsync();
        Assert.Null(await fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((player, Info)), TimeSpan.FromSeconds(7), true));
        await drain!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(fixture.Session.Player);
        Assert.Equal(1, player.Disposals);
    }

    /// <summary>Backend pause/resume callbacks cannot authorize transport publication for a replaced player.</summary>
    /// <param name="paused">Requested pause state.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantPauseOrResume_RejectsOldTransportPublication(bool paused)
    {
        var starts = new PlaybackStartOwnership();
        var session = new PlaybackSessionCoordinator(starts, new(), _ => { });
        var intent = starts.Begin();
        Assert.True(session.TryBegin(intent, default, out _));
        var old = new Output();
        if (!paused) old.Pause();
        Assert.True(session.TryAttach(intent, old));
        var replacement = new Output();
        void Replace()
        {
            Assert.Same(old, session.Detach());
            var next = starts.Begin();
            Assert.True(session.TryBegin(next, default, out _));
            Assert.True(session.TryAttach(next, replacement));
        }
        old.OnPause = Replace;
        old.OnResume = Replace;
        Assert.False(session.TrySetPaused(intent, old, paused));
        Assert.Same(replacement, session.Player);
        Assert.False(replacement.IsPaused);
        session.Detach();
    }

    /// <summary>Output switching at a simulated playing position preserves captured position and pause after complete release.</summary>
    /// <param name="seconds">Initial simulated playing position.</param>
    /// <param name="paused">Captured transport state.</param>
    /// <returns>The deterministic lifecycle test.</returns>
    [Theory]
    [InlineData(23, false)]
    [InlineData(23, true)]
    [InlineData(67, false)]
    [InlineData(67, true)]
    public async Task OutputSwitch_PreservesAdvancedPositionAndPause(int seconds, bool paused)
    {
        var fixture = new Lifecycle();
        var old = new Output(); var replacement = new Output();
        old.SeekRelease.SetResult();
        try
        {
            Assert.Same(old, await fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((old, Info)), TimeSpan.FromSeconds(seconds), paused));
            old.AdvanceTo(TimeSpan.FromSeconds(seconds + 2));
            var capturedPosition = old.Position; var capturedPause = old.IsPaused;
            var oldToken = fixture.Session.Token;
            var drain = fixture.StopAsync();
            await old.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(oldToken.IsCancellationRequested);
            Assert.Null(fixture.Session.Player);
            Assert.False(drain.IsCompleted);
            old.DisposeRelease.Set(); await drain.WaitAsync(TimeSpan.FromSeconds(10));
            var ready = fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((replacement, Info)), capturedPosition, capturedPause);
            await replacement.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(ready.IsCompleted);
            Assert.False(replacement.IsPaused);
            replacement.SeekRelease.SetResult();
            Assert.Same(replacement, await ready.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(TimeSpan.FromSeconds(seconds + 2), replacement.Position);
            Assert.Equal(paused, replacement.IsPaused);
            Assert.False(fixture.Session.Token.IsCancellationRequested);
            Assert.False(replacement.Completion.Task.IsCompleted);
            Assert.Equal(1, old.Disposals);
        }
        finally
        {
            old.SeekRelease.TrySetResult(); replacement.SeekRelease.TrySetResult();
            old.DisposeRelease.Set(); replacement.DisposeRelease.Set();
            await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, replacement.Disposals);
    }

    /// <summary>Queue edits during held gapless seek cause one new output using the final queue while retaining simulated position.</summary>
    /// <returns>The deterministic composed workflow test.</returns>
    [Fact]
    public async Task GaplessQueueEdit_RebuildsOwnedOutputAtCurrentPosition()
    {
        var fixture = new Lifecycle();
        var first = new Output(); var final = new Output();
        first.DisposeRelease.Set(); final.DisposeRelease.Set();
        var outputs = new Queue<Output>([first, final]);
        var queue = new List<int> { 1, 2, 3 };
        int[] prepared = [];
        var snapshots = new List<int[]>();
        async Task<bool> Observe(Task<IAudioPlayer?> ready) => (await ready) is not null;
        (long Intent, Task<bool> Ready) Prepare()
        {
            var position = fixture.Session.Player?.Position ?? TimeSpan.FromSeconds(42);
            var stop = fixture.StopAsync(full: false);
            Assert.True(stop.IsCompletedSuccessfully);
            prepared = queue.ToArray(); snapshots.Add(prepared);
            var player = outputs.Dequeue();
            var ready = fixture.Start(Task.FromResult<(IAudioPlayer, AudioFileInfo)?>((player, Info)), position, false);
            return (fixture.Intent, Observe(ready));
        }
        var refresh = new GaplessQueueRefreshCoordinator(() => true, () => null, Prepare,
            fixture.IsCurrent, () => prepared.SequenceEqual(queue));
        try
        {
            var ready = refresh.RefreshAsync();
            var firstToken = fixture.Session.Token;
            queue.Remove(2); queue.Add(4);
            Assert.Same(ready, refresh.RefreshAsync());
            first.SeekRelease.SetResult();
            await final.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(ready.IsCompleted);
            Assert.True(firstToken.IsCancellationRequested);
            Assert.Same(final, fixture.Session.Player);
            Assert.Equal(new[] { 1, 2, 3 }, snapshots[0]);
            Assert.Equal(new[] { 1, 3, 4 }, snapshots[1]);
            final.SeekRelease.SetResult();
            Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(TimeSpan.FromSeconds(42), final.Position);
            Assert.Equal(2, snapshots.Count);
            Assert.False(fixture.Session.Token.IsCancellationRequested);
            Assert.False(final.Completion.Task.IsCompleted);
        }
        finally
        {
            first.SeekRelease.TrySetResult(); final.SeekRelease.TrySetResult();
            first.DisposeRelease.Set(); final.DisposeRelease.Set();
            await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, first.Disposals); Assert.Equal(1, final.Disposals);
    }
    /// <summary>Supplies format metadata without probing media.</summary>
    private static AudioFileInfo Info => new("fixture", 48000, 2, 48000, false, "fixture", TimeSpan.FromSeconds(90));

    /// <summary>Composes production ownership/preparation/readiness/drain helpers with an injected held factory.</summary>
    private sealed class Lifecycle
    {
        private readonly PlaybackStartOwnership _starts = new();
        private readonly AudioPlayerRetirement _retirements = new(_ => { }, transitionBudget: TimeSpan.Zero);
        /// <summary>Owns preparation leases independently from audible completion.</summary>
        internal readonly PlaybackPreparationTracker Preparations = new();
        /// <summary>Owns the one active output.</summary>
        internal PlaybackSessionCoordinator Session { get; }
        /// <summary>Gets the most recently admitted audible lifetime.</summary>
        internal Task? Lifetime { get; private set; }
        /// <summary>Gets the current authoritative start intent for the refresh adapter.</summary>
        internal long Intent => _starts.Current;
        /// <summary>Checks whether a refresh still owns the admitted start.</summary>
        /// <param name="intent">Captured start.</param>
        /// <returns>Whether the captured start remains current.</returns>
        internal bool IsCurrent(long intent) => _starts.IsCurrent(intent);
        /// <summary>Creates window-free lifecycle composition.</summary>
        internal Lifecycle() => Session = new(_starts, new(), _ => { });

        /// <summary>Admits a controlled factory through real preparation and readiness ownership.</summary>
        /// <param name="factory">Controlled backend factory result.</param>
        /// <param name="position">Requested resume position.</param>
        /// <param name="paused">Initial pause state.</param>
        /// <returns>Readiness independently of the output lifetime.</returns>
        internal Task<IAudioPlayer?> Start(Task<(IAudioPlayer, AudioFileInfo)?> factory, TimeSpan position, bool paused)
        {
            var readiness = new PlaybackReadiness();
            Lifetime = _starts.RunAsync(async intent =>
            {
                using var preparation = Preparations.Begin();
                if (!Session.TryBegin(intent, preparation.Token, out var token)) return;
                var created = await _starts.CreateAsync(intent, _ => factory, token, _retirements);
                if (created is not { } result) return;
                var player = result.Player;
                if (!Session.TryAttach(intent, player)) { await _retirements.RetireAsync(player); return; }
                if (!await Session.SeekAsync(intent, player, position) || !Session.PrepareReadiness(intent, player, paused)) return;
                readiness.Publish(player);
                preparation.Dispose();
                await player.WaitForCompletionAsync();
            });
            return readiness.Observe(Lifetime, (_, _) => { });
        }

        /// <summary>Stops publication and composes ordinary or full output release.</summary>
        /// <param name="full">Whether pending preparation must drain.</param>
        /// <returns>The production release wait.</returns>
        internal Task StopAsync(bool full = true)
        {
            _starts.Invalidate();
            Preparations.CancelAll();
            return PlaybackOutputDrain.RetireAsync(Session.Detach(), full, _retirements, Preparations);
        }
    }

    /// <summary>Provides independently controlled seek, audible completion and worker disposal without hardware.</summary>
    private sealed class Output : IAudioPlayer
    {
        /// <summary>Simulates backend progress without completing playback or changing ownership.</summary>
        /// <param name="position">Current simulated playing position.</param>
        internal void AdvanceTo(TimeSpan position) => Position = position;
        /// <summary>Signals backend seek admission.</summary>
        internal readonly TaskCompletionSource SeekEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Completes or faults the controlled seek.</summary>
        internal readonly TaskCompletionSource SeekRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Controls audible completion independently from readiness.</summary>
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Signals worker disposal admission.</summary>
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Signals completed worker disposal.</summary>
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Allows worker disposal to finish.</summary>
        internal readonly ManualResetEventSlim DisposeRelease = new();
        /// <summary>Counts unique disposal calls.</summary>
        internal int Disposals;
        /// <summary>Optional synchronous pause callback.</summary>
        internal Action? OnPause;
        /// <summary>Optional synchronous resume callback.</summary>
        internal Action? OnResume;
        /// <inheritdoc/>
        public TimeSpan Duration => TimeSpan.FromSeconds(90);
        /// <inheritdoc/>
        public TimeSpan Position { get; private set; }
        /// <inheritdoc/>
        public bool IsPaused { get; private set; }
        /// <inheritdoc/>
        public bool CanSeek => true;
        /// <inheritdoc/>
        public float Volume { get; set; }
        /// <inheritdoc/>
        public float ReplayGainFactor { get; set; }
        /// <inheritdoc/>
        public void Pause() { IsPaused = true; OnPause?.Invoke(); }
        /// <inheritdoc/>
        public void Resume() { IsPaused = false; OnResume?.Invoke(); }
        /// <inheritdoc/>
        public async Task SeekAsync(TimeSpan position)
        {
            SeekEntered.TrySetResult();
            await SeekRelease.Task;
            Position = position;
        }
        /// <inheritdoc/>
        public Task WaitForCompletionAsync() => Completion.Task;
        /// <inheritdoc/>
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            DisposeEntered.TrySetResult();
            if (!DisposeRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic disposal gate");
            Completion.TrySetResult();
            Disposed.TrySetResult();
        }
    }
}
