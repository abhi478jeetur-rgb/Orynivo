using Orynivo.Streaming;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks every source setting that requires rejecting old catalog snapshots and requests.</summary>
public sealed class LibraryCatalogConfigurationTests
{
    /// <summary>Connection, identity, profile and playback setting changes must invalidate catalog rows.</summary>
    /// <param name="setting">Setting changed independently in this case.</param>
    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("url")]
    [InlineData("key")]
    [InlineData("profile")]
    [InlineData("format")]
    [InlineData("bitrate")]
    public void ChangedSetting_RejectsPreviousConfiguration(string setting)
    {
        var original = Server("one");
        var updated = Server("one");
        switch (setting)
        {
            case "id": updated.Id = "two"; break;
            case "name": updated.Name = "Renamed"; break;
            case "url": updated.BaseUrl = "http://other.invalid"; break;
            case "key": updated.ApiKey = "synthetic replacement"; break;
            case "profile": updated.ProfileId = "other"; break;
            case "format": updated.StreamingFormat = "aac"; break;
            case "bitrate": updated.StreamingBitrateKbps = 256; break;
        }
        Assert.False(LibraryCatalogConfiguration.MatchesServers([original], [updated]));
    }

    /// <summary>Identical copied sources and empty selections remain equivalent; additions/removals/reordering do not.</summary>
    [Fact]
    public void SourceSets_PreserveOrderedIdentityAndEmptySemantics()
    {
        Assert.True(LibraryCatalogConfiguration.MatchesServers(null, []));
        Assert.True(LibraryCatalogConfiguration.MatchesServers([Server("one")], [Server("one")]));
        Assert.False(LibraryCatalogConfiguration.MatchesServers([], [Server("one")]));
        Assert.False(LibraryCatalogConfiguration.MatchesServers([Server("one")], []));
        Assert.False(LibraryCatalogConfiguration.MatchesServers([Server("one"), Server("two")],
            [Server("two"), Server("one")]));
    }

    /// <summary>Creates an independent synthetic source configuration.</summary>
    /// <param name="id">Opaque source identity.</param>
    /// <returns>Settings with every compared field populated.</returns>
    private static OrynivoServerSettings Server(string id) => new()
    {
        Id = id, Name = "Fixture", BaseUrl = "http://fixture.invalid", ApiKey = "synthetic",
        ProfileId = "standard", StreamingFormat = "opus", StreamingBitrateKbps = 128
    };
}
