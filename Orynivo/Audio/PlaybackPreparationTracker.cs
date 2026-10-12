namespace Orynivo.Audio;

/// <summary>Tracks dispatcher-owned playback preparation independently of the audible playback lifetime.</summary>
internal sealed class PlaybackPreparationTracker
{
    private readonly HashSet<Preparation> _pending = [];

    /// <summary>Registers lookup, creation and initial seek until publication or obsolete-result cleanup finishes.</summary>
    /// <returns>The preparation lease whose disposal signals completion.</returns>
    internal Preparation Begin()
    {
        var preparation = new Preparation(this);
        _pending.Add(preparation);
        return preparation;
    }

    /// <summary>Requests cancellation without forgetting factories that ignore it.</summary>
    internal void CancelAll()
    {
        foreach (var preparation in _pending.ToArray())
            preparation.Cancel();
    }

    /// <summary>Waits for registered preparations, including cleanup of late-created players.</summary>
    /// <returns>Completion of all preparations present at the call.</returns>
    internal Task DrainAsync() => Task.WhenAll(_pending.Select(item => item.Completion).ToArray());

    /// <summary>A preparation cancellation source and completion lease, used only on the owning dispatcher.</summary>
    internal sealed class Preparation : IDisposable
    {
        private readonly PlaybackPreparationTracker _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        /// <summary>Creates a preparation lease for its tracker.</summary>
        /// <param name="owner">The dispatcher-owned tracker.</param>
        internal Preparation(PlaybackPreparationTracker owner) => _owner = owner;

        /// <summary>Gets the captured cancellation token for lookup and linked playback creation.</summary>
        internal CancellationToken Token => _cancellation.Token;

        /// <summary>Gets completion after publication or obsolete-result cleanup.</summary>
        internal Task Completion => _completion.Task;

        /// <summary>Requests cancellation of this preparation while retaining its completion lease.</summary>
        internal void Cancel()
        {
            if (!_disposed)
            {
                try { _cancellation.Cancel(); }
                catch (AggregateException)
                {
                    // Callback failures must not prevent cancellation of other preparations or forget cleanup.
                }
            }
        }

        /// <summary>Removes completed preparation and releases its cancellation source exactly once.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner._pending.Remove(this);
            _cancellation.Dispose();
            _completion.TrySetResult();
        }
    }
}
