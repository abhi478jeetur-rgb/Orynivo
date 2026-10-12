namespace Orynivo.Audio;

/// <summary>Owns dispatcher-affine active output and cancellation handoff for one playback session.</summary>
/// <param name="starts">Shared dispatcher-owned start generation.</param>
/// <param name="shutdown">Permanent output admission gate.</param>
/// <param name="reportCancellationFailure">Diagnostic sink for failing cancellation callbacks.</param>
internal sealed class PlaybackSessionCoordinator(PlaybackStartOwnership starts, PlaybackShutdownCoordinator shutdown,
    Action<Exception> reportCancellationFailure)
{
    private CancellationTokenSource? _cancellation;
    private long _intent;

    /// <summary>Gets the single active player, including while its initial seek is pending.</summary>
    internal IAudioPlayer? Player { get; private set; }

    /// <summary>Gets captured output cancellation, or an uncancellable token when output is detached.</summary>
    internal CancellationToken Token => _cancellation?.Token ?? CancellationToken.None;

    /// <summary>Admits output preparation only for a current start with an empty session.</summary>
    /// <param name="intent">Start ownership captured before lookup and retirement.</param>
    /// <param name="preparationToken">Cancellation linked to the owning preparation.</param>
    /// <param name="token">Captured output token, or the default token when admission fails.</param>
    /// <returns>Whether this start acquired output cancellation ownership.</returns>
    internal bool TryBegin(long intent, CancellationToken preparationToken, out CancellationToken token)
    {
        token = default;
        if (shutdown.IsShuttingDown || !starts.IsCurrent(intent) || preparationToken.IsCancellationRequested ||
            _cancellation is not null || Player is not null)
            return false;
        _intent = intent;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(preparationToken);
        token = _cancellation.Token;
        return true;
    }

    /// <summary>Transfers a factory result to the current session; rejected players remain caller-owned.</summary>
    /// <param name="intent">Start that created this output.</param>
    /// <param name="player">Unpublished factory result.</param>
    /// <returns>Whether the caller transferred unique player ownership.</returns>
    internal bool TryAttach(long intent, IAudioPlayer player)
    {
        if (!IsCurrent(intent) || Player is not null) return false;
        Player = player;
        return true;
    }

    /// <summary>Checks both session intent and active identity before publishing or changing prepared output.</summary>
    /// <param name="intent">Owning playback start.</param>
    /// <param name="player">Expected active output.</param>
    /// <returns>Whether this live session still owns the supplied player.</returns>
    internal bool Owns(long intent, IAudioPlayer player) => IsCurrent(intent) && ReferenceEquals(Player, player);

    /// <summary>Applies a best-effort resume seek and rejects completion after stop or replacement.</summary>
    /// <param name="intent">Owning preparation start.</param>
    /// <param name="player">Expected active output.</param>
    /// <param name="position">Requested initial or saved position.</param>
    /// <returns>Whether output ownership remains current after the attempted seek.</returns>
    internal async Task<bool> SeekAsync(long intent, IAudioPlayer player, TimeSpan position)
    {
        if (!Owns(intent, player)) return false;
        if (position > TimeSpan.Zero && player.CanSeek)
        {
            try { await player.SeekAsync(position); }
            catch { /* Resume failure must not prevent current playback. */ }
        }
        return Owns(intent, player);
    }

    /// <summary>Applies initial pause only to current output and checks ownership again after backend callbacks.</summary>
    /// <param name="intent">Owning preparation start.</param>
    /// <param name="player">Expected fully sought output.</param>
    /// <param name="initiallyPaused">Whether to pause before readiness publication.</param>
    /// <returns>Whether the caller may publish readiness for this output.</returns>
    internal bool PrepareReadiness(long intent, IAudioPlayer player, bool initiallyPaused)
    {
        if (!Owns(intent, player)) return false;
        return !initiallyPaused || TrySetPaused(intent, player, true);
    }

    /// <summary>Changes pause state on the expected output and rejects reentrant backend ownership changes.</summary>
    /// <param name="intent">Owning playback start.</param>
    /// <param name="player">Expected active output.</param>
    /// <param name="paused">Requested pause state.</param>
    /// <returns>Whether the caller may publish the resulting transport state.</returns>
    internal bool TrySetPaused(long intent, IAudioPlayer player, bool paused)
    {
        if (!Owns(intent, player)) return false;
        if (paused && !player.IsPaused) player.Pause();
        else if (!paused && player.IsPaused) player.Resume();
        return Owns(intent, player);
    }

    /// <summary>Detaches a stale gapless snapshot while preserving cancellation for another preparation attempt.</summary>
    /// <param name="intent">Owning preparation intent.</param>
    /// <param name="player">Expected active player, never a replacement player.</param>
    /// <returns>The uniquely detached player, or null if ownership has changed.</returns>
    internal IAudioPlayer? DetachForRetry(long intent, IAudioPlayer player)
    {
        if (!IsCurrent(intent) || !ReferenceEquals(Player, player)) return null;
        Player = null;
        return player;
    }

    /// <summary>Clears output ownership before cancellation callbacks and returns the uniquely detached player.</summary>
    /// <returns>The former player, or null for an empty or already detached session.</returns>
    internal IAudioPlayer? Detach()
    {
        var player = Player;
        var cancellation = _cancellation;
        Player = null;
        _cancellation = null;
        try { cancellation?.Cancel(); }
        catch (AggregateException error)
        {
            // A failing callback must not strand the detached player outside retirement.
            try { reportCancellationFailure(error); } catch { }
        }
        finally { cancellation?.Dispose(); }
        return player;
    }

    /// <summary>Checks output admission after an asynchronous factory or seek boundary.</summary>
    /// <param name="intent">Owning start identifier.</param>
    /// <returns>Whether this start still owns live output cancellation and may attach a player.</returns>
    private bool IsCurrent(long intent) => !shutdown.IsShuttingDown && starts.IsCurrent(intent) &&
        _intent == intent && _cancellation is { IsCancellationRequested: false };
}
