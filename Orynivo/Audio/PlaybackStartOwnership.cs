namespace Orynivo.Audio;

/// <summary>Tracks dispatcher-owned start intents without duplicating active player or queue state.</summary>
internal sealed class PlaybackStartOwnership
{
    private long _generation;

    /// <summary>Supersedes earlier start intents while leaving active playback under window ownership.</summary>
    /// <returns>The new start identifier.</returns>
    internal long Begin() => ++_generation;

    /// <summary>Abandons pending starts when playback is stopped.</summary>
    internal void Invalidate() => ++_generation;

    /// <summary>Checks whether an intent may publish playback state.</summary>
    /// <param name="intent">The captured start identifier.</param>
    /// <returns>Whether no later start or stop has superseded it.</returns>
    internal bool IsCurrent(long intent) => intent == _generation;

    /// <summary>Runs a new intent and suppresses failures from superseded playback lifetimes.</summary>
    /// <param name="operation">The playback lifetime receiving its owning identifier.</param>
    /// <returns>The operation lifetime; current failures propagate to its caller.</returns>
    internal async Task RunAsync(Func<long, Task> operation)
    {
        var intent = Begin();
        try
        {
            await operation(intent);
        }
        catch (Exception) when (!IsCurrent(intent))
        {
            // Existing caller error handlers may stop playback; obsolete failures must not reach them.
        }
    }

    /// <summary>Retires a late factory result instead of transferring it to active playback.</summary>
    /// <param name="intent">The captured start identifier.</param>
    /// <param name="factory">The backend factory, supplied the captured token.</param>
    /// <param name="cancellationToken">The owning playback token.</param>
    /// <param name="retirement">The shared detached-player disposal owner.</param>
    /// <returns>The current player and format, or null for an obsolete or unavailable result.</returns>
    internal async Task<(IAudioPlayer Player, AudioFileInfo Info)?> CreateAsync(
        long intent,
        Func<CancellationToken, Task<(IAudioPlayer Player, AudioFileInfo Info)?>> factory,
        CancellationToken cancellationToken,
        AudioPlayerRetirement retirement)
    {
        if (!IsCurrent(intent))
            return null;
        var created = await factory(cancellationToken);
        if (created is { } result && !IsCurrent(intent))
        {
            await retirement.RetireAsync(result.Player);
            return null;
        }
        return created;
    }
}
