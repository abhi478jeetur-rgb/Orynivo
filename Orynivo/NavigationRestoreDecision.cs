namespace Orynivo;

/// <summary>Identifies the existing source-specific Back restoration adapter.</summary>
internal enum NavigationRestoreKind
{
    /// <summary>Restore an ordinary view through its normal top-level loader.</summary>
    TopLevel,
    /// <summary>Restore all source parts of a logical album.</summary>
    LogicalAlbum,
    /// <summary>Restore an album in the local catalog.</summary>
    LocalAlbum,
    /// <summary>Restore an artist in the local catalog.</summary>
    LocalArtist,
    /// <summary>Restore a name-based artist across libraries.</summary>
    UnifiedArtist,
    /// <summary>Restore an album through its saved server adapter.</summary>
    ServerAlbum,
    /// <summary>Restore an artist through its saved server adapter.</summary>
    ServerArtist,
    /// <summary>Restore the search page and its query.</summary>
    Search,
    /// <summary>Restore the Genre Cloud branch and mode.</summary>
    GenreCloud
}

/// <summary>Contains scalar eligibility inputs without owning history, source settings or rows.</summary>
/// <param name="View">Saved view discriminator.</param>
/// <param name="SelectedId">Saved provider-local entity identifier.</param>
/// <param name="ArtistId">Saved provider-local artist identifier.</param>
/// <param name="ArtistName">Saved unified artist name.</param>
/// <param name="HasLogicalParts">Whether the saved logical album has source parts.</param>
internal sealed record NavigationRestoreContext(
    string View, long? SelectedId, long? ArtistId, string? ArtistName, bool HasLogicalParts);

/// <summary>Selects existing restore adapters without an Avalonia window or a second history model.</summary>
internal static class NavigationRestoreDecision
{
    /// <summary>Preserves discriminator eligibility and the existing top-level fallback.</summary>
    /// <param name="context">Scalar inputs from the window-owned history payload.</param>
    /// <returns>The eligible adapter; source validation remains in the server adapters.</returns>
    internal static NavigationRestoreKind Select(NavigationRestoreContext context) => context.View switch
    {
        "LogicalAlbumTracks" when context.SelectedId.HasValue && context.HasLogicalParts => NavigationRestoreKind.LogicalAlbum,
        "AlbumTracks" when context.SelectedId.HasValue => NavigationRestoreKind.LocalAlbum,
        "ArtistAlbums" when context.ArtistId.HasValue => NavigationRestoreKind.LocalArtist,
        "UnifiedArtistAlbums" when !string.IsNullOrWhiteSpace(context.ArtistName) => NavigationRestoreKind.UnifiedArtist,
        "OrynivoAlbumTracks" when context.SelectedId.HasValue => NavigationRestoreKind.ServerAlbum,
        "OrynivoArtistAlbums" when context.ArtistId.HasValue => NavigationRestoreKind.ServerArtist,
        "Search" => NavigationRestoreKind.Search,
        "GenreCloud" => NavigationRestoreKind.GenreCloud,
        _ => NavigationRestoreKind.TopLevel
    };
}
