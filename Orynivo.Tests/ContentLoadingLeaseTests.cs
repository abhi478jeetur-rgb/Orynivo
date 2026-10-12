using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects balanced loading release independently of UI and navigation.</summary>
public sealed class ContentLoadingLeaseTests
{
    /// <summary>Cancellation and completion release a contribution once.</summary>
    [Fact]
    public void RepeatedReleaseIsIdempotent()
    {
        var releases = 0;
        var lease = new ContentLoadingLease(() => releases++);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, releases);
    }

    /// <summary>Competing release calls cannot decrement the shared count twice.</summary>
    /// <returns>A task completing after all competing release calls.</returns>
    [Fact]
    public async Task ConcurrentReleaseRunsAdapterOnce()
    {
        var releases = 0;
        var lease = new ContentLoadingLease(() => Interlocked.Increment(ref releases));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(lease.Dispose)));
        Assert.Equal(1, releases);
    }

    /// <summary>A failing release adapter is not invoked again during later cleanup.</summary>
    [Fact]
    public void FailedReleaseIsNotRepeated()
    {
        var releases = 0;
        var lease = new ContentLoadingLease(() => { releases++; throw new InvalidOperationException(); });
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        lease.Dispose();
        Assert.Equal(1, releases);
    }
}
