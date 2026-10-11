namespace Orynivo;

/// <summary>Identifies the existing Back capture route without owning history or UI state.</summary>
internal enum NavigationCaptureKind
{
    /// <summary>No selectable view is active.</summary>
    None,
    /// <summary>The visible search page takes precedence over detail state.</summary>
    Search,
    /// <summary>The visible Genre Cloud retains its branch and mode.</summary>
    GenreCloud,
    /// <summary>A logical album retains all provider-local parts.</summary>
    LogicalAlbum,
    /// <summary>An album belongs to the active Orynivo Server catalog.</summary>
    ServerAlbum,
    /// <summary>An album uses the local catalog.</summary>
    LocalAlbum,
    /// <summary>An artist is scoped to an Orynivo Server navigation tag.</summary>
    ServerArtist,
    /// <summary>An artist uses the local catalog without a detail provider.</summary>
    LocalArtist,
    /// <summary>A name-based artist combines matching libraries.</summary>
    UnifiedArtist,
    /// <summary>An ordinary selectable sidebar view is active.</summary>
    TopLevel
}

/// <summary>Captures only scalar route inputs; it owns no rows, providers, credentials or Back stack.</summary>
/// <param name="SearchVisible">Whether the search page is visible.</param>
/// <param name="GenreCloudVisible">Whether the Genre Cloud surface is visible.</param>
/// <param name="TopLevelTag">Existing sidebar identity.</param>
/// <param name="AlbumId">Active provider-local album identifier.</param>
/// <param name="ArtistId">Active provider-local artist identifier.</param>
/// <param name="ArtistName">Active artist name for unified navigation.</param>
/// <param name="HasLogicalAlbumParts">Whether the active album contains source-aware parts.</param>
/// <param name="IsServerAlbum">Whether the active catalog album has server source identity.</param>
/// <param name="ServerId">Active server identity, excluding its connection settings.</param>
/// <param name="HasAlbumProvider">Whether an album detail provider is active.</param>
internal sealed record NavigationCaptureContext(
    bool SearchVisible,
    bool GenreCloudVisible,
    string? TopLevelTag,
    long? AlbumId,
    long? ArtistId,
    string? ArtistName,
    bool HasLogicalAlbumParts,
    bool IsServerAlbum,
    string? ServerId,
    bool HasAlbumProvider);

/// <summary>Returns a typed capture route and its existing credential-free server navigation tag.</summary>
/// <param name="Kind">Route chosen using the existing capture precedence.</param>
/// <param name="NavigationTag">Server tag when required; ordinary view tags remain in the window.</param>
internal sealed record NavigationCaptureTarget(NavigationCaptureKind Kind, string? NavigationTag = null);

/// <summary>Selects source-aware Back capture routes without an Avalonia window.</summary>
internal static class NavigationCaptureDecision
{
    /// <summary>Applies capture precedence without treating equal numeric IDs as equal sources.</summary>
    /// <param name="context">Scalar inputs captured synchronously by the window.</param>
    /// <returns>The existing capture route, or None for non-selectable section state.</returns>
    internal static NavigationCaptureTarget Select(NavigationCaptureContext context)
    {
        if (context.SearchVisible) return new(NavigationCaptureKind.Search);
        if (context.TopLevelTag == "GenreCloud" && context.GenreCloudVisible)
            return new(NavigationCaptureKind.GenreCloud);
        var serverTag = context.TopLevelTag?.StartsWith("OrynivoServer:", StringComparison.Ordinal) == true;
        if (context.AlbumId.HasValue)
        {
            if (context.HasLogicalAlbumParts) return new(NavigationCaptureKind.LogicalAlbum);
            if (context.IsServerAlbum && context.ServerId is not null)
                return new(NavigationCaptureKind.ServerAlbum, serverTag ? context.TopLevelTag
                    : $"OrynivoServer:{context.ServerId}:Albums");
            return new(NavigationCaptureKind.LocalAlbum);
        }
        if (context.ArtistId.HasValue)
        {
            if (serverTag) return new(NavigationCaptureKind.ServerArtist, context.TopLevelTag);
            if (!context.HasAlbumProvider) return new(NavigationCaptureKind.LocalArtist);
        }
        else if (!string.IsNullOrWhiteSpace(context.ArtistName) && context.TopLevelTag == "Albums")
            return new(NavigationCaptureKind.UnifiedArtist);
        return !string.IsNullOrWhiteSpace(context.TopLevelTag) &&
               !context.TopLevelTag.StartsWith("Section:", StringComparison.Ordinal)
            ? new(NavigationCaptureKind.TopLevel)
            : new(NavigationCaptureKind.None);
    }
}
