namespace Orynivo;

/// <summary>Orders complete manual image writes per normalized artist within one window.</summary>
internal sealed class ArtistImageWriteQueue
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);

    /// <summary>Gets the number of artists with active or waiting work.</summary>
    internal int ActiveArtistCount { get { lock (_sync) return _tails.Count; } }

    /// <summary>Reserves ordered work immediately and rechecks ownership after previous work finishes.</summary>
    /// <param name="artistKey">Normalized artist identity, retained only in memory.</param>
    /// <param name="canWrite">Checks admission on the caller's captured synchronization context.</param>
    /// <param name="write">Completes the entire local and remote mutation batch.</param>
    /// <returns>Whether current work ran successfully; failures propagate without blocking later work.</returns>
    internal Task<bool> RunAsync(string artistKey, Func<bool> canWrite, Func<Task<bool>> write)
    {
        Task previous;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            previous = _tails.GetValueOrDefault(artistKey, Task.CompletedTask);
            _tails[artistKey] = completion.Task;
        }
        return ExecuteAsync(artistKey, previous, completion, canWrite, write);
    }

    /// <summary>Runs admitted work and releases its queue position even after failure or abandonment.</summary>
    /// <param name="artistKey">The reserved artist identity.</param>
    /// <param name="previous">Completion signal for the preceding batch.</param>
    /// <param name="completion">This batch's completion signal, independent of its success.</param>
    /// <param name="canWrite">Checks the original action's ownership after waiting.</param>
    /// <param name="write">The complete mutation batch.</param>
    /// <returns>Whether the batch ran successfully.</returns>
    private async Task<bool> ExecuteAsync(string artistKey, Task previous, TaskCompletionSource completion,
        Func<bool> canWrite, Func<Task<bool>> write)
    {
        try
        {
            await previous;
            return canWrite() && await write();
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_tails.GetValueOrDefault(artistKey), completion.Task))
                    _tails.Remove(artistKey);
                completion.SetResult();
            }
        }
    }
}
