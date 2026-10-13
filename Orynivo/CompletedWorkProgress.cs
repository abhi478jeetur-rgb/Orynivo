namespace Orynivo;

/// <summary>Counts completed assembly steps without estimating elapsed time or remaining duration.</summary>
internal sealed class CompletedWorkProgress
{
    private readonly int _total;
    private int _completed;

    /// <summary>Creates a fixed, monotonic work-step counter.</summary>
    /// <param name="total">Known number of assembly steps.</param>
    internal CompletedWorkProgress(int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(total);
        _total = total;
    }

    /// <summary>Gets the bounded percentage of successfully completed steps.</summary>
    internal int Percentage => (int)Math.Min(100L, 100L * Volatile.Read(ref _completed) / _total);

    /// <summary>Records one successfully completed assembly step.</summary>
    internal void CompleteStep() => Interlocked.Increment(ref _completed);

    /// <summary>Counts an asynchronous step only after successful completion.</summary>
    /// <typeparam name="T">Step result type.</typeparam>
    /// <param name="task">Admitted assembly work.</param>
    /// <returns>The original result after updating the counter.</returns>
    internal async Task<T> TrackAsync<T>(Task<T> task)
    {
        var result = await task.ConfigureAwait(false);
        CompleteStep();
        return result;
    }
}
