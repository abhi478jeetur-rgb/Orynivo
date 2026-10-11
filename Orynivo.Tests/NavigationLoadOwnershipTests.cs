using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects view publication across overlapping navigation without constructing a window.</summary>
public sealed class NavigationLoadOwnershipTests
{
    /// <summary>A same-view replacement rejects the old request independently of cancellation.</summary>
    [Fact]
    public void NewLoadRejectsPreviousAndForeignRequests()
    {
        var owner = new NavigationLoadOwnership();
        var earlier = owner.Begin(CancellationToken.None);
        var current = owner.Begin(CancellationToken.None);
        Assert.False(owner.IsCurrent(earlier));
        Assert.True(owner.IsCurrent(current));
        Assert.False(new NavigationLoadOwnership().IsCurrent(current));
    }

    /// <summary>Leaving a view abandons publication while permitting a later return.</summary>
    [Fact]
    public void NavigationInvalidationAllowsRecovery()
    {
        var owner = new NavigationLoadOwnership();
        var earlier = owner.Begin(CancellationToken.None);
        owner.Invalidate();
        Assert.False(owner.IsCurrent(earlier));
        Assert.True(owner.IsCurrent(owner.Begin(CancellationToken.None)));
    }

    /// <summary>Cancellation rejects the current request even without newer navigation.</summary>
    [Fact]
    public void CancellationRejectsCurrentPublication()
    {
        var owner = new NavigationLoadOwnership();
        using var cancellation = new CancellationTokenSource();
        var request = owner.Begin(cancellation.Token);
        cancellation.Cancel();
        Assert.False(owner.IsCurrent(request));
    }

    /// <summary>A cancellation callback admitting newer work keeps its replacement ownership.</summary>
    [Fact]
    public void ReentrantCancellationRetainsNewestAdmission()
    {
        var owner = new NavigationLoadOwnership();
        using var earlierCancellation = new CancellationTokenSource();
        owner.Begin(earlierCancellation.Token);
        NavigationLoadOwnership.Request? reentrant = null;
        using var registration = earlierCancellation.Token.Register(() => reentrant = owner.Begin(CancellationToken.None));
        var interrupted = owner.Begin(CancellationToken.None);
        earlierCancellation.Cancel();
        Assert.False(owner.IsCurrent(interrupted));
        Assert.True(owner.IsCurrent(reentrant!));
    }
}
