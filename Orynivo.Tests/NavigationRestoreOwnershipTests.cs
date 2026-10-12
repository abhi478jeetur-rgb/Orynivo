using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects overlapping Back execution, independent navigation and flowing nested ownership.</summary>
public sealed class NavigationRestoreOwnershipTests
{
    /// <summary>Old completion cannot clear the executing flag or publication ownership of a newer restore.</summary>
    [Fact]
    public void OldCompletionCannotEndReplacement()
    {
        var owner = new NavigationRestoreOwnership();
        var old = owner.Begin();
        var latest = owner.Begin();
        owner.Complete(old);
        Assert.True(owner.IsRestoring);
        Assert.False(owner.IsCurrent(old));
        Assert.True(owner.IsCurrent(latest));
        owner.Complete(latest);
        Assert.False(owner.IsRestoring);
        Assert.True(owner.IsCurrent(latest));
        Assert.True(owner.TryAdmitNavigation());
        Assert.False(owner.IsCurrent(latest));
    }

    /// <summary>Independent navigation resumes history capture immediately and rejects pending layout work.</summary>
    [Fact]
    public void IndependentNavigationAbandonsExecutingRestore()
    {
        var owner = new NavigationRestoreOwnership();
        var restore = owner.Begin();
        Assert.False(owner.ShouldSuppressHistory);
        Assert.True(owner.TryAdmitNavigation());
        Assert.False(owner.IsRestoring);
        Assert.False(owner.IsCurrent(restore));
        owner.Complete(restore);
        Assert.False(owner.IsRestoring);
    }

    /// <summary>Obsolete nested work cannot abandon the current restore or acquire its history suppression.</summary>
    [Fact]
    public void ObsoleteContextCannotAdmitOrInvalidateNewerWork()
    {
        var owner = new NavigationRestoreOwnership();
        var old = owner.Begin();
        var latest = owner.Begin();
        owner.Context = old;
        Assert.False(owner.TryAdmitNavigation());
        Assert.False(owner.ShouldSuppressHistory);
        Assert.True(owner.IsCurrent(latest));
        Assert.True(owner.IsRestoring);
        owner.Context = latest;
        Assert.True(owner.TryAdmitNavigation());
        Assert.True(owner.ShouldSuppressHistory);
        Assert.True(owner.IsCurrent(latest));
    }

    /// <summary>Restore context survives awaits without leaking into unrelated callers.</summary>
    /// <returns>A task completing after both isolated asynchronous scopes finish.</returns>
    [Fact]
    public async Task ContextFlowsOnlyThroughItsOwnCallChain()
    {
        var owner = new NavigationRestoreOwnership();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Restore()
        {
            var request = owner.Begin();
            owner.Context = request;
            Assert.True(owner.ShouldSuppressHistory);
            await gate.Task;
            Assert.True(owner.ShouldSuppressHistory);
            Assert.True(owner.TryAdmitNavigation());
            owner.Complete(request);
            owner.Context = null;
        }
        var task = Restore();
        Assert.Null(owner.Context);
        Assert.False(owner.ShouldSuppressHistory);
        gate.SetResult();
        await task;
        Assert.Null(owner.Context);
        Assert.False(owner.IsRestoring);
    }
}
