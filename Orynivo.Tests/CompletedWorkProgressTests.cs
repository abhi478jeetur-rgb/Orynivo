using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks honest completed-step percentages without clocks or UI.</summary>
public sealed class CompletedWorkProgressTests
{
    /// <summary>Held work does not advance and successful work advances by its fixed share.</summary>
    /// <returns>A task completing after the held step.</returns>
    [Fact]
    public async Task HeldAndCompletedSteps_HaveDeterministicPercentages()
    {
        var progress = new CompletedWorkProgress(4);
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = progress.TrackAsync(gate.Task);
        Assert.Equal(0, progress.Percentage);
        gate.SetResult(7);
        Assert.Equal(7, await pending);
        Assert.Equal(25, progress.Percentage);
        progress.CompleteStep();
        Assert.Equal(50, progress.Percentage);
        progress.CompleteStep(); progress.CompleteStep(); progress.CompleteStep();
        Assert.Equal(100, progress.Percentage);
    }

    /// <summary>Failed and cancelled steps never falsely report completed work.</summary>
    /// <returns>A task completing after both rejected steps.</returns>
    [Fact]
    public async Task FailedAndCancelledSteps_DoNotAdvance()
    {
        var progress = new CompletedWorkProgress(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => progress.TrackAsync(Task.FromException<int>(new InvalidOperationException())));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progress.TrackAsync(Task.FromCanceled<int>(new CancellationToken(true))));
        Assert.Equal(0, progress.Percentage);
    }

    /// <summary>Concurrent source completions retain their exact contribution.</summary>
    /// <returns>A task completing after all synthetic workers.</returns>
    [Fact]
    public async Task ConcurrentCompletions_AreBoundedAndCounted()
    {
        var progress = new CompletedWorkProgress(100);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(progress.CompleteStep)));
        Assert.Equal(100, progress.Percentage);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletedWorkProgress(0));
    }
}
