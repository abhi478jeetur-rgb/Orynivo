using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects source identity and capture precedence without constructing a window.</summary>
public sealed class NavigationCaptureDecisionTests
{
    private static NavigationCaptureContext Empty => new(false, false, "Tracks", null, null,
        null, false, false, null, false);

    /// <summary>Visible search and cloud pages take precedence over retained detail fields.</summary>
    [Fact]
    public void VisiblePagesOverrideRetainedDetailState()
    {
        var detail = Empty with { AlbumId = 7, ArtistId = 8, HasLogicalAlbumParts = true,
            IsServerAlbum = true, ServerId = "server-a", TopLevelTag = "GenreCloud", GenreCloudVisible = true };
        Assert.Equal(NavigationCaptureKind.Search, NavigationCaptureDecision.Select(detail with { SearchVisible = true }).Kind);
        Assert.Equal(NavigationCaptureKind.GenreCloud, NavigationCaptureDecision.Select(detail).Kind);
        Assert.Equal(NavigationCaptureKind.LogicalAlbum, NavigationCaptureDecision.Select(detail with { GenreCloudVisible = false }).Kind);
        Assert.Equal(NavigationCaptureKind.LogicalAlbum, NavigationCaptureDecision.Select(detail with { TopLevelTag = "Albums" }).Kind);
    }

    /// <summary>Dashboard remote albums never restore through a colliding local identifier.</summary>
    [Fact]
    public void EqualAlbumIdsRetainTheirOwningSource()
    {
        var local = Empty with { AlbumId = 42, TopLevelTag = "Dashboard" };
        var remote = local with { IsServerAlbum = true, ServerId = "server-a" };
        var localTarget = NavigationCaptureDecision.Select(local);
        var remoteTarget = NavigationCaptureDecision.Select(remote);
        Assert.Equal(NavigationCaptureKind.LocalAlbum, localTarget.Kind);
        Assert.Null(localTarget.NavigationTag);
        Assert.Equal(NavigationCaptureKind.ServerAlbum, remoteTarget.Kind);
        Assert.Equal("OrynivoServer:server-a:Albums", remoteTarget.NavigationTag);
        Assert.Equal("OrynivoServer:server-b:Albums",
            NavigationCaptureDecision.Select(remote with { ServerId = "server-b" }).NavigationTag);
        Assert.Equal(NavigationCaptureKind.LogicalAlbum,
            NavigationCaptureDecision.Select(remote with { HasLogicalAlbumParts = true }).Kind);
    }

    /// <summary>Existing remote navigation tags remain intact and cannot alone reclassify local albums.</summary>
    [Fact]
    public void AlbumCatalogSourceOverridesSidebarOrigin()
    {
        var album = Empty with { AlbumId = 42, TopLevelTag = "OrynivoServer:server-a:Tracks" };
        Assert.Equal(NavigationCaptureKind.LocalAlbum, NavigationCaptureDecision.Select(album).Kind);
        var remote = album with { IsServerAlbum = true, ServerId = "server-a" };
        Assert.Equal(album.TopLevelTag, NavigationCaptureDecision.Select(remote).NavigationTag);
        Assert.Equal(NavigationCaptureKind.LocalAlbum,
            NavigationCaptureDecision.Select(remote with { ServerId = null }).Kind);
    }

    /// <summary>Provider-local artist IDs and name-based unified artists keep different routes.</summary>
    [Fact]
    public void ArtistRoutesRespectSourceAndProviderState()
    {
        var artist = Empty with { ArtistId = 42, ArtistName = "Fixture artist", TopLevelTag = "Albums" };
        Assert.Equal(NavigationCaptureKind.LocalArtist, NavigationCaptureDecision.Select(artist).Kind);
        var remote = artist with { TopLevelTag = "OrynivoServer:server-a:Artists", HasAlbumProvider = true };
        Assert.Equal(NavigationCaptureKind.ServerArtist, NavigationCaptureDecision.Select(remote).Kind);
        Assert.Equal(remote.TopLevelTag, NavigationCaptureDecision.Select(remote).NavigationTag);
        Assert.Equal(NavigationCaptureKind.TopLevel,
            NavigationCaptureDecision.Select(artist with { HasAlbumProvider = true }).Kind);
        Assert.Equal(NavigationCaptureKind.UnifiedArtist,
            NavigationCaptureDecision.Select(artist with { ArtistId = null }).Kind);
        Assert.Equal(NavigationCaptureKind.LocalAlbum,
            NavigationCaptureDecision.Select(artist with { AlbumId = 42 }).Kind);
    }

    /// <summary>Only selectable top-level tags enter the ordinary Back capture route.</summary>
    /// <param name="tag">Current sidebar identity.</param>
    /// <param name="expected">Whether ordinary capture is permitted.</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("Section:Library", false)]
    [InlineData("Playlist:17", true)]
    [InlineData("Plex:fixture:albums", true)]
    [InlineData("Tracks", true)]
    public void OrdinaryCaptureRejectsSectionOnlyState(string? tag, bool expected)
    {
        Assert.Equal(expected ? NavigationCaptureKind.TopLevel : NavigationCaptureKind.None,
            NavigationCaptureDecision.Select(Empty with { TopLevelTag = tag }).Kind);
    }

    /// <summary>Whitespace artist names do not manufacture a unified detail target.</summary>
    [Fact]
    public void EmptyArtistNameRetainsOrdinaryAlbumsRoute()
    {
        Assert.Equal(NavigationCaptureKind.TopLevel,
            NavigationCaptureDecision.Select(Empty with { TopLevelTag = "Albums", ArtistName = " " }).Kind);
    }
}
