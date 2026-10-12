using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects restore source routing and incomplete history fallback.</summary>
public sealed class NavigationRestoreDecisionTests
{
    /// <summary>Equal numeric identifiers cannot change the saved source or detail kind.</summary>
    /// <param name="view">Saved view discriminator.</param>
    /// <param name="expected">Expected source-specific adapter.</param>
    [Theory]
    [InlineData("AlbumTracks", NavigationRestoreKind.LocalAlbum)]
    [InlineData("OrynivoAlbumTracks", NavigationRestoreKind.ServerAlbum)]
    [InlineData("ArtistAlbums", NavigationRestoreKind.LocalArtist)]
    [InlineData("OrynivoArtistAlbums", NavigationRestoreKind.ServerArtist)]
    [InlineData("LogicalAlbumTracks", NavigationRestoreKind.LogicalAlbum)]
    [InlineData("UnifiedArtistAlbums", NavigationRestoreKind.UnifiedArtist)]
    [InlineData("Search", NavigationRestoreKind.Search)]
    [InlineData("GenreCloud", NavigationRestoreKind.GenreCloud)]
    public void SavedDiscriminatorRetainsSource(string view, object expected)
    {
        Assert.Equal((NavigationRestoreKind)expected, NavigationRestoreDecision.Select(new(view, 42, 42, "Artist", true)));
    }

    /// <summary>Missing required detail identity preserves the original top-level fallback.</summary>
    /// <param name="view">Incomplete detail or ordinary view discriminator.</param>
    [Theory]
    [InlineData("AlbumTracks")]
    [InlineData("OrynivoAlbumTracks")]
    [InlineData("ArtistAlbums")]
    [InlineData("OrynivoArtistAlbums")]
    [InlineData("LogicalAlbumTracks")]
    [InlineData("UnifiedArtistAlbums")]
    [InlineData("Albums")]
    [InlineData("Artists")]
    [InlineData("Plex:library")]
    [InlineData("Playlist:7")]
    public void IncompleteOrOrdinaryStateUsesTopLevel(string view)
    {
        Assert.Equal(NavigationRestoreKind.TopLevel,
            NavigationRestoreDecision.Select(new(view, null, null, " ", false)));
    }

    /// <summary>A logical identifier alone cannot manufacture source parts or a local route.</summary>
    [Fact]
    public void LogicalAlbumRequiresBothIdentityAndParts()
    {
        Assert.Equal(NavigationRestoreKind.TopLevel,
            NavigationRestoreDecision.Select(new("LogicalAlbumTracks", 42, 42, "Artist", false)));
        Assert.Equal(NavigationRestoreKind.TopLevel,
            NavigationRestoreDecision.Select(new("LogicalAlbumTracks", null, 42, "Artist", true)));
    }
}
