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
    /// <summary>Observing a running request does not abandon it, while a later admission rejects the observation.</summary>
    [Fact]
    public void ObservationRetainsRunningLoadUntilReplacement()
    {
        var owner = new NavigationLoadOwnership();
        var running = owner.Begin(CancellationToken.None);
        var observation = owner.CaptureCurrent();
        Assert.True(owner.IsCurrent(running));
        Assert.True(owner.IsCurrent(observation));
        owner.Begin(CancellationToken.None);
        Assert.False(owner.IsCurrent(observation));
    }

    /// <summary>Observations survive idle capture and are rejected when navigation invalidates the owner.</summary>
    [Fact]
    public void IdleObservationRejectsNavigationInvalidation()
    {
        var owner = new NavigationLoadOwnership();
        var observation = owner.CaptureCurrent();
        Assert.True(owner.IsCurrent(observation));
        owner.Invalidate();
        Assert.False(owner.IsCurrent(observation));
        Assert.True(owner.IsCurrent(owner.CaptureCurrent()));
    }
}
