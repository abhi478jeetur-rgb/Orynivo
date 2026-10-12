namespace Orynivo.Audio;

/// <summary>Coalesces dispatcher-owned gapless refresh requests without owning a queue, player, or UI.</summary>
internal sealed class GaplessQueueRefreshCoordinator
{
    private readonly Func<bool> _canRefresh;
    private readonly Func<Task<bool>?> _joinPreparation;
    private readonly Func<(long Intent, Task<bool> Ready)> _prepare;
    private readonly Func<long, bool> _isCurrent;
    private readonly Func<bool> _isSnapshotCurrent;
    private RefreshRun? _pending;

    /// <summary>Injects the authoritative playback adapters; callbacks and requests run on the owning dispatcher.</summary>
    /// <param name="canRefresh">Whether active output needs a gapless rebuild.</param>
    /// <param name="joinPreparation">Existing start preparation to join, or null.</param>
    /// <param name="prepare">Starts preparation synchronously and returns its intent and guarded readiness.</param>
    /// <param name="isCurrent">Checks whether a captured playback intent still owns output.</param>
    /// <param name="isSnapshotCurrent">Checks whether preparation already incorporated all admitted edits.</param>
    internal GaplessQueueRefreshCoordinator(
        Func<bool> canRefresh, Func<Task<bool>?> joinPreparation,
        Func<(long Intent, Task<bool> Ready)> prepare,
        Func<long, bool> isCurrent, Func<bool> isSnapshotCurrent)
    {
        _canRefresh = canRefresh ?? throw new ArgumentNullException(nameof(canRefresh));
        _joinPreparation = joinPreparation ?? throw new ArgumentNullException(nameof(joinPreparation));
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _isSnapshotCurrent = isSnapshotCurrent ?? throw new ArgumentNullException(nameof(isSnapshotCurrent));
    }

    /// <summary>Joins current refresh work or existing preparation, otherwise starts one rebuild loop.</summary>
    /// <returns>Whether final output remains current; preparation failures propagate to every joining caller.</returns>
    internal Task<bool> RefreshAsync()
    {
        if (_pending is { } pending &&
            (pending.Intent is null || _isCurrent(pending.Intent.Value)))
        {
            ++pending.Revision;
            return pending.Completion.Task;
        }
        if (_joinPreparation() is { } preparing) return preparing;
        if (!_canRefresh()) return Task.FromResult(true);
        var run = new RefreshRun();
        _pending = run;
        _ = RebuildAsync(run);
        return run.Completion.Task;
    }

    /// <summary>Retries changed revisions only while this run's preparation remains current.</summary>
    /// <param name="run">Independent state for this refresh, retained when newer work supersedes it.</param>
    /// <returns>The observed refresh lifetime; all outcomes complete its shared result.</returns>
    private async Task RebuildAsync(RefreshRun run)
    {
        try
        {
            while (true)
            {
                var revision = run.Revision;
                var (intent, ready) = _prepare();
                run.Intent = intent;
                if (!await ready || !_isCurrent(intent))
                {
                    run.Completion.TrySetResult(false);
                    return;
                }
                if (revision == run.Revision || _isSnapshotCurrent())
                {
                    run.Completion.TrySetResult(true);
                    return;
                }
            }
        }
        catch (OperationCanceledException error) { run.Completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { run.Completion.TrySetException(error); }
        finally
        {
            if (ReferenceEquals(_pending, run)) _pending = null;
        }
    }

    /// <summary>Keeps one run's revision and result independent of subsequently admitted refreshes.</summary>
    private sealed class RefreshRun
    {
        /// <summary>Preparation intent, or null while the first synchronous adapter is executing.</summary>
        internal long? Intent;
        /// <summary>Number of additional edits admitted to this run.</summary>
        internal long Revision;
        /// <summary>Shared readiness result for all admitted callers.</summary>
        internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
