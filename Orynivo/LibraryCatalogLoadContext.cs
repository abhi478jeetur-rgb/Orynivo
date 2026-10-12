using System.Collections.Frozen;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Captures transient catalog inputs without retaining a second row catalog or logging connection details.</summary>
internal sealed class LibraryCatalogLoadContext
{
    /// <summary>Copies configuration and personal selections before source work starts.</summary>
    /// <param name="servers">Configured remote connections; copied for this operation only.</param>
    /// <param name="favorites">Personal remote favorite identities.</param>
    /// <param name="filters">Already copied track facet selections.</param>
    /// <param name="artistFavoritesOnly">Artist favorite filter.</param>
    /// <param name="albumFavoritesOnly">Album favorite filter.</param>
    /// <param name="albumArtwork">Whether local album artwork is required.</param>
    /// <param name="artistId">Optional local artist drilldown.</param>
    /// <param name="albumId">Optional local album drilldown.</param>
    internal LibraryCatalogLoadContext(IEnumerable<OrynivoServerSettings> servers, IEnumerable<string> favorites,
        TrackFacetFilterSnapshot filters, bool artistFavoritesOnly, bool albumFavoritesOnly,
        bool albumArtwork, long? artistId, long? albumId)
    {
        Servers = servers.Select(server => new OrynivoServerSettings
        {
            Id = server.Id, Name = server.Name, BaseUrl = server.BaseUrl, ApiKey = server.ApiKey,
            ProfileId = server.ProfileId, StreamingFormat = server.StreamingFormat,
            StreamingBitrateKbps = server.StreamingBitrateKbps
        }).ToArray();
        Favorites = favorites.ToFrozenSet(StringComparer.Ordinal);
        Filters = filters;
        ArtistFavoritesOnly = artistFavoritesOnly;
        AlbumFavoritesOnly = albumFavoritesOnly;
        AlbumArtwork = albumArtwork;
        ArtistId = artistId;
        AlbumId = albumId;
    }

    /// <summary>Gets operation-owned remote connection copies, never persisted or logged.</summary>
    internal IReadOnlyList<OrynivoServerSettings> Servers { get; }
    /// <summary>Gets immutable personal favorite identities.</summary>
    internal IReadOnlySet<string> Favorites { get; }
    /// <summary>Gets copied track selections.</summary>
    internal TrackFacetFilterSnapshot Filters { get; }
    /// <summary>Gets the captured artist favorite filter.</summary>
    internal bool ArtistFavoritesOnly { get; }
    /// <summary>Gets the captured album favorite filter.</summary>
    internal bool AlbumFavoritesOnly { get; }
    /// <summary>Gets the captured local album artwork requirement.</summary>
    internal bool AlbumArtwork { get; }
    /// <summary>Gets the captured local artist scope.</summary>
    internal long? ArtistId { get; }
    /// <summary>Gets the captured local album scope.</summary>
    internal long? AlbumId { get; }
}
