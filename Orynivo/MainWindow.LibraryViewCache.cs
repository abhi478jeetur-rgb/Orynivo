namespace Orynivo;

public partial class MainWindow
{
    private readonly LibraryCatalogLoadCoordinator<ContentRow> _unifiedLibraryLoads = new(3);
    private LibraryLoadResult<ContentRow>? _unifiedLibraryLoadResult;

    /// <summary>Clears catalog snapshots and abandons pending searches while retaining published search failures.</summary>
    private void InvalidateUnifiedLibraryViewCache()
    {
        CancelLibrarySearch(clearResult: false);
        _unifiedLibraryLoads.Invalidate();
        InvalidateSimilarityFeatureCache();
    }

    /// <summary>Determines whether the current view state is safe to reuse as an unfiltered snapshot.</summary>
    /// <param name="tag">Shared library view tag.</param>
    /// <returns><see langword="true"/> for an unfiltered top-level catalog view.</returns>
    private bool CanCacheUnifiedLibraryView(string tag) =>
        (tag is "Artists" or "Albums" or "Tracks") &&
        !HasActiveFilters &&
        !_artistFavoritesOnly &&
        !_albumFavoritesOnly &&
        _activeArtistFilterId is null &&
        _activeAlbumFilterId is null;

    /// <summary>Builds a non-persisted cache key without retaining server URLs or credentials.</summary>
    /// <param name="tag">Shared library view tag.</param>
    /// <returns>The current view cache identity.</returns>
    private string CreateUnifiedLibraryViewCacheKey(string tag)
    {
        var servers = string.Join('|', (_settings.OrynivoServers ?? [])
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => $"{server.Id}:{StringComparer.Ordinal.GetHashCode(server.BaseUrl ?? string.Empty)}"));
        var artworkMode = tag switch
        {
            "Albums" => _showAlbumArtworkView,
            "Artists" => _showArtistArtworkView,
            _ => false
        };
        return $"{tag};{artworkMode};{servers}";
    }
}
