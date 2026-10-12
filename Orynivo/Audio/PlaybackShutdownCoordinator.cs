namespace Orynivo.Audio;

/// <summary>Closes dispatcher-owned playback admission permanently and shares one shutdown outcome.</summary>
internal sealed class PlaybackShutdownCoordinator
{
    private TaskCompletionSource? _completion;

    /// <summary>Gets whether shutdown has been admitted, including while hooks run or after failure.</summary>
    internal bool IsShuttingDown => _completion is not null;

    /// <summary>Blocks admission before invoking hooks and runs shutdown exactly once, including reentrant requests.</summary>
    /// <param name="onBegin">Synchronous window/queue shutdown hooks, invoked after admission closes.</param>
    /// <param name="drain">Complete output drain on the owning dispatcher.</param>
    /// <returns>The shared shutdown task; success, cancellation and failure remain shared on subsequent requests.</returns>
    internal Task BeginAsync(Action onBegin, Func<Task> drain)
    {
        if (_completion is { } existing) return existing.Task;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _ = RunAsync(completion, onBegin, drain);
        return completion.Task;
    }

    /// <summary>Observes synchronous hooks and asynchronous drain into the published shared outcome.</summary>
    /// <param name="completion">Outcome installed before callbacks are invoked.</param>
    /// <param name="onBegin">Synchronous shutdown hooks.</param>
    /// <param name="drain">Complete output drain.</param>
    /// <returns>The observed operation lifetime; failures complete the shared task.</returns>
    private static async Task RunAsync(TaskCompletionSource completion, Action onBegin, Func<Task> drain)
    {
        try
        {
            onBegin();
            await drain();
            completion.TrySetResult();
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
