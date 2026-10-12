namespace Orynivo.Audio;

/// <summary>Tracks detached players until background disposal finishes, independently of UI and queue state.</summary>
internal sealed class AudioPlayerRetirement
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _pending = [];
    private readonly Action<Exception> _reportFailure;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _transitionBudget;

    /// <summary>Creates a retirement owner with a bounded ordinary-transition wait.</summary>
    /// <param name="reportFailure">Existing application error sink; failures in this sink are isolated.</param>
    /// <param name="timeProvider">Clock used for transition waits, or the system clock.</param>
    /// <param name="transitionBudget">Ordinary transition wait, defaulting to two seconds.</param>
    internal AudioPlayerRetirement(Action<Exception> reportFailure, TimeProvider? timeProvider = null,
        TimeSpan? transitionBudget = null)
    {
        ArgumentNullException.ThrowIfNull(reportFailure);
        _reportFailure = reportFailure;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transitionBudget = transitionBudget ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThan(_transitionBudget, TimeSpan.Zero);
    }

    /// <summary>Disposes a detached player on a worker and optionally drains all previously retired players.</summary>
    /// <param name="player">Uniquely detached player, or null when no active player remains.</param>
    /// <param name="waitForCompleteDisposal">Whether every retirement registered so far must finish.</param>
    /// <returns>A bounded transition wait or complete device-release wait; errors reach only the supplied sink.</returns>
    internal async Task RetireAsync(IAudioPlayer? player, bool waitForCompleteDisposal = false)
    {
        Task? retirement = null;
        Task wait;
        lock (_sync)
        {
            if (player is not null)
            {
                retirement = Task.Run(() =>
                {
                    try { player.Dispose(); }
                    catch (Exception exception)
                    {
                        try { _reportFailure(exception); }
                        catch { /* Error reporting must not prevent device-release completion. */ }
                    }
                });
                _pending.Add(retirement);
                _ = retirement.ContinueWith(completed =>
                {
                    lock (_sync) _pending.Remove(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            wait = waitForCompleteDisposal ? Task.WhenAll(_pending.ToArray()) : retirement ?? Task.CompletedTask;
        }
        if (waitForCompleteDisposal || wait.IsCompleted)
        {
            await wait.ConfigureAwait(false);
            return;
        }
        using var budget = new CancellationTokenSource();
        try
        {
            await Task.WhenAny(wait, Task.Delay(_transitionBudget, _timeProvider, budget.Token)).ConfigureAwait(false);
        }
        finally { budget.Cancel(); }
    }
}
