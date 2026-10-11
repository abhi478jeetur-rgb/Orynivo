using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Best-effort source artwork synchronization with navigation-aware publication adapters.</summary>
public partial class MainWindow
{
    private const int SynchronizedArtworkMaximumBytes = 20 * 1024 * 1024;

    /// <summary>Copies an existing artist image into matching local or remote identities that lack one.</summary>
    /// <param name="artistName">Unified artist display name.</param>
    /// <param name="sources">Matching source-specific artist records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the best-effort synchronization.</returns>
    private async Task SynchronizeMissingArtistArtworkAsync(
        string artistName,
        IReadOnlyList<(LibraryCatalogArtist Artist, OrynivoServerSettings? Server)> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count < 2 || sources.All(HasArtistArtwork))
            return;

        (LibraryCatalogArtist Artist, OrynivoServerSettings? Server) source = default;
        (byte[] Data, string MimeType)? image = null;
        foreach (var candidate in sources
            .Where(HasArtistArtwork)
            .OrderByDescending(candidate => candidate.Artist.ImageIsManual)
            .ThenByDescending(candidate => candidate.Artist.ProfileFetchedAt ?? 0))
        {
            image = await TryReadArtworkBytesAsync(candidate.Artist.ArtworkPath, cancellationToken);
            if (image is null)
                continue;
            source = candidate;
            break;
        }
        if (source.Artist is null || image is null)
            return;

        var changed = false;
        foreach (var target in sources.Where(candidate => !HasArtistArtwork(candidate)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (target.Server is null)
                {
                    var imagePath = await ArtistImageSearchService.SaveImageAsync(
                        target.Artist.Id,
                        image.Value.Data,
                        image.Value.MimeType,
                        cancellationToken);
                    await Task.Run(() =>
                    {
                        using var db = AudioDatabase.OpenDefault();
                        if (source.Artist.ImageIsManual || target.Artist.ImageIsManual)
                            db.UpdateArtistImage(target.Artist.Id, imagePath);
                        else
                            db.UpdateArtistProfile(
                                target.Artist.Id,
                                target.Artist.Biography,
                                imagePath,
                                target.Artist.SourceUrl,
                                target.Artist.ProfileLanguage ?? GetProfileLanguageCode());
                    }, cancellationToken);
                    changed = true;
                }
                else if (source.Artist.ImageIsManual || target.Artist.ImageIsManual)
                {
                    changed |= await _orynivoClient.UploadArtistImageAsync(
                        target.Server,
                        target.Artist.Id,
                        image.Value.Data,
                        image.Value.MimeType,
                        cancellationToken);
                    DeleteOrynivoArtistListCache(target.Server);
                }
                else
                {
                    var updated = await _orynivoClient.UpdateArtistProfileAsync(
                        target.Server,
                        target.Artist.Id,
                        target.Artist.Biography,
                        target.Artist.SourceUrl,
                        target.Artist.ProfileLanguage ?? GetProfileLanguageCode(),
                        image.Value.Data,
                        image.Value.MimeType,
                        cancellationToken);
                    changed |= updated is not null;
                    DeleteOrynivoArtistListCache(target.Server);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A temporarily unavailable source must not block artist navigation.
            }
        }

        if (changed)
            InvalidateUnifiedLibraryViewCache();
    }

    /// <summary>Copies existing artwork into equivalent album identities that currently lack it.</summary>
    /// <param name="sources">Source-specific album records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="canContinue">Optional ownership guard for write admission.</param>
    /// <param name="onCatalogInvalidated">Optional notification for this operation's own cache invalidation.</param>
    /// <returns>Refreshed source records containing newly stored artwork paths.</returns>
    private async Task<List<(LibraryCatalogAlbum Album, OrynivoServerSettings? Server)>> SynchronizeMissingAlbumArtworkAsync(
        IReadOnlyList<(LibraryCatalogAlbum Album, OrynivoServerSettings? Server)> sources,
        CancellationToken cancellationToken,
        Func<bool>? canContinue = null,
        Action? onCatalogInvalidated = null)
    {
        var result = sources.ToList();
        var changed = false;
        bool CanContinue() => !cancellationToken.IsCancellationRequested && (canContinue?.Invoke() ?? true);
        try
        {
            var groups = result
                .Select((source, index) => (source, index))
                .GroupBy(item =>
                    $"{ArtistNameNormalizer.CreateComparisonKey(item.source.Album.Title)}|" +
                    $"{ArtistNameNormalizer.CreateComparisonKey(item.source.Album.DisplayArtist)}",
                    StringComparer.Ordinal);

            foreach (var group in groups)
            {
                if (!CanContinue()) return result;
                var entries = group.ToList();
                if (entries.Count < 2 || entries.All(item => HasAlbumArtwork(item.source)))
                    continue;

                (byte[] Data, string MimeType)? image = null;
                foreach (var candidate in entries.Where(item => HasAlbumArtwork(item.source)))
                {
                    if (!CanContinue()) return result;
                    image = await TryReadArtworkBytesAsync(
                        candidate.source.Album.ArtworkPath ?? candidate.source.Album.ThumbnailPath,
                        cancellationToken);
                    if (!CanContinue()) return result;
                    if (image is not null)
                        break;
                }
                if (image is null)
                    continue;

                foreach (var target in entries.Where(item => !HasAlbumArtwork(item.source)))
                {
                    if (!CanContinue()) return result;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        ILibraryCatalogProvider provider = target.source.Server is null
                            ? _localCatalogProvider
                            : CreateOrynivoCatalogProvider(target.source.Server);
                        if (!await provider.SetAlbumArtworkAsync(
                                target.source.Album.Id,
                                image.Value.Data,
                                image.Value.MimeType,
                                cancellationToken))
                        {
                            continue;
                        }

                        changed = true;
                        if (target.source.Server is not null)
                            DeleteOrynivoAlbumListCache(target.source.Server);
                        if (!CanContinue()) return result;
                        var refreshed = await provider.GetAlbumAsync(
                            target.source.Album.Id,
                            includeArtwork: true,
                            cancellationToken);
                        if (!CanContinue()) return result;
                        if (refreshed is not null)
                            result[target.index] = (refreshed, target.source.Server);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Synchronization is best effort for unreachable remote libraries.
                    }
                }
            }

            return result;
        }
        finally
        {
            if (changed)
            {
                var notifyOwner = canContinue?.Invoke() ?? true;
                InvalidateUnifiedLibraryViewCache();
                if (notifyOwner) onCatalogInvalidated?.Invoke();
            }
        }
    }

    /// <summary>Synchronizes and applies artwork for the source identities represented by an opened album.</summary>
    /// <param name="row">Unified album row used by the detail header.</param>
    /// <param name="parts">Local and remote album identities.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="canPublish">Guard checked before source work and header row hydration.</param>
    /// <param name="onCatalogInvalidated">Notification for synchronization's own cache generation advance.</param>
    /// <returns>A task representing synchronization and row refresh.</returns>
    private async Task SynchronizeLogicalAlbumArtworkAsync(
        ContentRow row,
        IReadOnlyList<LogicalAlbumPart> parts,
        CancellationToken cancellationToken,
        Func<bool> canPublish,
        Action onCatalogInvalidated)
    {
        if (parts.Count < 2 || cancellationToken.IsCancellationRequested || !canPublish())
            return;

        var sources = new List<(LibraryCatalogAlbum Album, OrynivoServerSettings? Server)>();
        foreach (var part in parts)
        {
            if (cancellationToken.IsCancellationRequested || !canPublish()) return;
            ILibraryCatalogProvider provider = part.Server is null
                ? _localCatalogProvider
                : CreateOrynivoCatalogProvider(part.Server);
            try
            {
                var album = await provider.GetAlbumAsync(part.AlbumId, includeArtwork: true, cancellationToken);
                if (cancellationToken.IsCancellationRequested || !canPublish()) return;
                if (album is not null) sources.Add((album, part.Server));
            }
            catch
            {
                // The detail remains usable with every source that did respond.
            }
        }

        if (cancellationToken.IsCancellationRequested || !canPublish()) return;
        var synchronized = await SynchronizeMissingAlbumArtworkAsync(sources, cancellationToken,
            canPublish, onCatalogInvalidated);
        if (cancellationToken.IsCancellationRequested || !canPublish()) return;
        var artwork = synchronized.FirstOrDefault(HasAlbumArtwork);
        if (artwork.Album is null)
            return;

        row.ArtworkPath = artwork.Album.ArtworkPath;
        row.ThumbnailPath = artwork.Album.ThumbnailPath;
        row.Artwork = null;
        row.Thumbnail = null;
        row.ArtworkLoadQueued = false;
        row.ArtworkLoadCompleted = false;
        row.ThumbnailLoadQueued = false;
        row.ThumbnailLoadCompleted = false;
        EnsureArtworkHydrated(row);
    }

    /// <summary>Determines whether an album record points to available artwork.</summary>
    /// <param name="album">Album record.</param>
    /// <returns><see langword="true"/> when an artwork or thumbnail path is present.</returns>
    private static bool HasAlbumArtwork((LibraryCatalogAlbum Album, OrynivoServerSettings? Server) source) =>
        source.Server is null
            ? (!string.IsNullOrWhiteSpace(source.Album.ArtworkPath) && File.Exists(source.Album.ArtworkPath)) ||
              (!string.IsNullOrWhiteSpace(source.Album.ThumbnailPath) && File.Exists(source.Album.ThumbnailPath))
            : !string.IsNullOrWhiteSpace(source.Album.ArtworkPath) ||
              !string.IsNullOrWhiteSpace(source.Album.ThumbnailPath);

    /// <summary>Determines whether an artist source points to usable artwork.</summary>
    /// <param name="source">Source-specific artist record.</param>
    /// <returns><see langword="true"/> when an image is available.</returns>
    private static bool HasArtistArtwork((LibraryCatalogArtist Artist, OrynivoServerSettings? Server) source) =>
        source.Server is null
            ? !string.IsNullOrWhiteSpace(source.Artist.ArtworkPath) && File.Exists(source.Artist.ArtworkPath)
            : !string.IsNullOrWhiteSpace(source.Artist.ArtworkPath);

    /// <summary>Reads bounded artwork bytes from a local cache path or authenticated server URL.</summary>
    /// <param name="path">Local path or HTTP URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image bytes and detected MIME type, or <see langword="null"/>.</returns>
    private static async Task<(byte[] Data, string MimeType)?> TryReadArtworkBytesAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            byte[] data;
            if (IsHttpUrl(path))
            {
                using var response = await RemoteArtworkHttpClient.GetAsync(
                    path,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > SynchronizedArtworkMaximumBytes)
                    return null;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    if (destination.Length + read > SynchronizedArtworkMaximumBytes)
                        return null;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                data = destination.ToArray();
            }
            else
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length <= 0 || file.Length > SynchronizedArtworkMaximumBytes)
                    return null;
                data = await File.ReadAllBytesAsync(path, cancellationToken);
            }

            return data.Length == 0 ? null : (data, DetectArtworkMimeType(data));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Detects the supported artwork MIME type from its leading bytes.</summary>
    /// <param name="data">Image bytes.</param>
    /// <returns>A MIME type accepted by local and remote artwork stores.</returns>
    private static string DetectArtworkMimeType(byte[] data) =>
        data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? "image/png"
            : data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)
                ? "image/webp"
                : "image/jpeg";
}
