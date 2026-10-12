using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies bounded active artist image intent without retaining completed mutation history.</summary>
public sealed class ArtistImageIntentOwnershipTests
{
    /// <summary>Later unrelated admissions cannot undo rejection of an earlier matching image.</summary>
    [Fact]
    public void RejectionSurvivesOtherArtistWrites()
    {
        var owner = new ArtistImageIntentOwnership();
        using var first = owner.Observe((null, 1, "Artist A"));
        using var second = owner.Observe((null, 2, "Artist B"));
        owner.Admit(null, 1, "Artist A");
        Assert.True(first.WasSupersededFor(null, 1, "Artist A"));
        Assert.False(second.WasSupersededFor(null, 2, "Artist B"));
        owner.Admit(null, 2, "Artist B");
        Assert.True(first.WasSupersededFor(null, 1, "Artist A"));
        Assert.True(second.WasSupersededFor(null, 2, "Artist B"));
    }

    /// <summary>Source-local identity matches renames while separating colliding IDs from other sources.</summary>
    /// <param name="source">The incoming source ID.</param>
    /// <param name="matches">Whether the source-local identity matches.</param>
    [Theory]
    [InlineData("one", true)]
    [InlineData("two", false)]
    [InlineData(null, false)]
    public void SourceLocalIdentityIsRetained(string? source, bool matches)
    {
        var owner = new ArtistImageIntentOwnership();
        using var observation = owner.Observe(("one", 1, "Before rename"));
        owner.Admit(source, 1, "After rename");
        Assert.Equal(matches, observation.WasSupersededFor("one", 1, "Before rename"));
    }

    /// <summary>Normalized canonical names preserve matching identity across different sources and IDs.</summary>
    [Fact]
    public void NamesMatchAcrossSources()
    {
        var owner = new ArtistImageIntentOwnership();
        using var observation = owner.Observe((null, 1, "Björk"));
        owner.Admit("remote", 701, "BJORK");
        owner.Admit("remote", 702, "Other artist");
        Assert.True(observation.WasSupersededFor(null, 1, "Björk"));
        Assert.False(observation.WasSupersededFor(null, 2, "Other artist"));
    }

    /// <summary>Multiple observed targets keep independent rejection flags.</summary>
    [Fact]
    public void MultipleTargetsRemainIndependent()
    {
        var owner = new ArtistImageIntentOwnership();
        using var observation = owner.Observe((null, 1, "A"), (null, 2, "B"));
        owner.Admit(null, 1, "A");
        Assert.True(observation.WasSupersededFor(null, 1, "A"));
        Assert.False(observation.WasSupersededFor(null, 2, "B"));
    }

    /// <summary>Own propagation renewal affects only the renewing operation; new observations start current.</summary>
    [Fact]
    public void RenewalAndNewObservationDoNotReviveOtherWork()
    {
        var owner = new ArtistImageIntentOwnership();
        using var old = owner.Observe((null, 1, "A"));
        using var propagating = owner.Observe((null, 1, "A"));
        owner.Admit(null, 1, "A"); propagating.Renew();
        using var fresh = owner.Observe((null, 1, "A"));
        Assert.True(old.WasSupersededFor(null, 1, "A"));
        Assert.False(propagating.WasSupersededFor(null, 1, "A"));
        Assert.False(fresh.WasSupersededFor(null, 1, "A"));
        owner.Admit(null, 1, "A");
        Assert.True(propagating.WasSupersededFor(null, 1, "A"));
        Assert.True(fresh.WasSupersededFor(null, 1, "A"));
    }

    /// <summary>Disposal releases completed identities and rejects late callbacks without keeping artist history.</summary>
    [Fact]
    public void DisposalRemovesCompletedObservations()
    {
        var owner = new ArtistImageIntentOwnership();
        var observation = owner.Observe((null, 1, "A"));
        Assert.Equal(1, owner.ActiveObservationCount);
        observation.Dispose(); observation.Dispose(); observation.Renew();
        Assert.Equal(0, owner.ActiveObservationCount);
        Assert.True(observation.WasSupersededFor(null, 1, "A"));
        owner.Admit(null, 1, "A");
        using var fresh = owner.Observe((null, 1, "A"));
        Assert.False(fresh.WasSupersededFor(null, 1, "A"));
    }

    /// <summary>Captured input identities remain fixed even when the caller changes its array.</summary>
    [Fact]
    public void CallerCannotReplaceCapturedTargets()
    {
        var owner = new ArtistImageIntentOwnership();
        var targets = new (string?, long?, string?)[] { (null, 1, "A") };
        using var observation = owner.Observe(targets);
        targets[0] = (null, 2, "B");
        owner.Admit(null, 1, "A");
        Assert.True(observation.WasSupersededFor(null, 1, "A"));
    }

    /// <summary>Unknown IDs and empty names never act as a global matching identity.</summary>
    [Fact]
    public void UnknownIdentityDoesNotPoisonOtherWork()
    {
        var owner = new ArtistImageIntentOwnership();
        using var observation = owner.Observe((null, null, null));
        owner.Admit(null, null, null);
        Assert.False(observation.WasSupersededFor(null, null, null));
    }

    /// <summary>Resolved canonical metadata adds cross-source matching without using a previous artist's name or undoing rejection.</summary>
    [Fact]
    public void CanonicalBindingPreservesSourceIdentityAndRejection()
    {
        var owner = new ArtistImageIntentOwnership();
        using var observation = owner.Observe((null, 1, null));
        owner.Admit(null, 2, "Previous artist");
        observation.BindCanonicalName(null, 1, "Current artist");
        Assert.False(observation.WasSupersededFor(null, 1, "Current artist"));
        owner.Admit("remote", 701, "Current artist");
        observation.BindCanonicalName(null, 1, "Renamed current artist");
        Assert.True(observation.WasSupersededFor(null, 1, "Renamed current artist"));
    }
}
