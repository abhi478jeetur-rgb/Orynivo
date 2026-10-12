using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies stable source-owned download cache identities without touching catalogs or files.</summary>
public sealed class ServerArtistProfileCacheIdentityTests
{
    /// <summary>Server image staging cannot reuse positive local artist cache identifiers.</summary>
    /// <param name="artistId">A representative source-local ID.</param>
    [Theory]
    [InlineData(1L)]
    [InlineData(701L)]
    [InlineData(long.MaxValue)]
    public void ServerDownloadsUseNegativeIdentitySpace(long artistId)
        => Assert.True(ServerArtistProfileCacheIdentity.Create("source-a", artistId) < 0);

    /// <summary>The same source and artist remain stable across repeated requests and cultures.</summary>
    [Fact]
    public void IdentityIsStableAndCultureIndependent()
    {
        var expected = ServerArtistProfileCacheIdentity.Create("source-a", 701);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("hi-IN");
            Assert.Equal(expected, ServerArtistProfileCacheIdentity.Create("source-a", 701));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>Colliding numeric artist IDs on different sources retain independent downloaded caches.</summary>
    [Fact]
    public void SourceAndArtistBothContributeToIdentity()
    {
        var first = ServerArtistProfileCacheIdentity.Create("source-a", 701);
        Assert.NotEqual(first, ServerArtistProfileCacheIdentity.Create("source-b", 701));
        Assert.NotEqual(first, ServerArtistProfileCacheIdentity.Create("source-a", 702));
    }
}
