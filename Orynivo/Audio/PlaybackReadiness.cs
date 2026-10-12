namespace Orynivo.Audio;

/// <summary>Separates dispatcher-owned player readiness from the full playback lifetime without owning the active player.</summary>
internal sealed class PlaybackReadiness
{
    private readonly TaskCompletionSource<IAudioPlayer?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IAudioPlayer? _published;

    /// <summary>Publishes the prepared player after the caller has checked current ownership.</summary>
    /// <param name="player">The prepared player, with initial seek and pause already applied.</param>
    internal void Publish(IAudioPlayer player)
    {
        _published = player;
        _ready.TrySetResult(player);
    }

    /// <summary>Observes lifetime errors while allowing callers to return when preparation finishes.</summary>
    /// <param name="lifetime">The complete playback lifetime.</param>
    /// <param name="reportPlaybackFailure">Handles post-readiness errors, checking active ownership before changing UI.</param>
    /// <returns>The prepared player, null for an abandoned/unavailable start, or a pre-readiness error.</returns>
    internal Task<IAudioPlayer?> Observe(Task lifetime, Action<IAudioPlayer, Exception> reportPlaybackFailure)
    {
        _ = ObserveLifetimeAsync(lifetime, reportPlaybackFailure);
        return _ready.Task;
    }

    /// <summary>Routes preparation errors to the awaiting caller and later errors to its guarded callback.</summary>
    /// <param name="lifetime">The complete playback lifetime.</param>
    /// <param name="reportPlaybackFailure">The guarded post-readiness error sink.</param>
    /// <returns>The observation lifetime; reporting failures are isolated.</returns>
    private async Task ObserveLifetimeAsync(Task lifetime, Action<IAudioPlayer, Exception> reportPlaybackFailure)
    {
        try { await lifetime; }
        catch (Exception error)
        {
            if (!_ready.TrySetException(error) && _published is { } player)
            {
                try { reportPlaybackFailure(player, error); }
                catch { /* A reporting failure must not fault the detached observer. */ }
            }
        }
        finally { _ready.TrySetResult(null); }
    }
}
