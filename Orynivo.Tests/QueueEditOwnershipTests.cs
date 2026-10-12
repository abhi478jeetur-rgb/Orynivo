using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects queue publication independently of audio devices and UI.</summary>
public sealed class QueueEditOwnershipTests
{
    /// <summary>Independent edits share ownership until a destructive change.</summary>
    [Fact]
    public void Invalidate_CancelsOldEditsAndAdmitsNewEdits()
    {
        using var owner = new QueueEditOwnership();
        var first = owner.Capture();
        var second = owner.Capture();
        Assert.True(owner.IsCurrent(first.Version));
        Assert.Equal(first.Version, second.Version);
        owner.Invalidate();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(owner.IsCurrent(first.Version));
        var current = owner.Capture();
        Assert.True(owner.IsCurrent(current.Version));
        Assert.False(current.Token.IsCancellationRequested);
    }

    /// <summary>A failing cancellation subscriber cannot prevent invalidation.</summary>
    [Fact]
    public void Invalidate_IsolatesCancellationCallbackFailure()
    {
        using var owner = new QueueEditOwnership();
        var old = owner.Capture();
        using var registration = old.Token.Register(() => throw new InvalidOperationException());
        owner.Invalidate();
        Assert.False(owner.IsCurrent(old.Version));
        Assert.True(owner.IsCurrent(owner.Capture().Version));
    }

    /// <summary>Shutdown rejects late results and future work, and repeated disposal is safe.</summary>
    [Fact]
    public void Dispose_RejectsPublicationAndCapture()
    {
        var owner = new QueueEditOwnership();
        var old = owner.Capture();
        owner.Dispose();
        owner.Dispose();
        owner.Invalidate();
        Assert.True(old.Token.IsCancellationRequested);
        Assert.False(owner.IsCurrent(old.Version));
        Assert.Throws<ObjectDisposedException>(() => owner.Capture());
    }
}
