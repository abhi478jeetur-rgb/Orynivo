using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Best-effort source artwork synchronization with navigation-aware publication adapters.</summary>
public partial class MainWindow
{
    private const int SynchronizedArtworkMaximumBytes = 20 * 1024 * 1024;

    /// <summary>Copies current artist artwork only into still-missing identities in shared write order.</summary>
    /// <param name="artistName">Original unified artist identity.</param>
    /// <param name="sources">Original matching local and remote records.</param>
    /// <param name="cancellationToken">Cancellation for optional synchronization.</param>
    /// <param name="canContinue">Optional enclosing detail/navigation ownership.</param>
    /// <returns>A task completing current best-effort synchronization or abandonment.</returns>
    private async Task SynchronizeMissingArtistArtworkAsync(string artistName,
        IReadOnlyList<(LibraryCatalogArtist Artist, OrynivoServerSettings? Server)> sources,
        CancellationToken cancellationToken, Func<bool>? canContinue = null)
    {
        if (sources.Count < 2) return;
        var key = ArtistNameNormalizer.CreateComparisonKey(artistName);
        if (key.Length == 0) return;
        var manualImage = _artistImageWrites.CaptureCurrent();
        var captured = sources.Select(item =>
        {
            var original = item.Server;
            var connection = original is null ? null : new OrynivoServerSettings
            {
                Id = original.Id, Name = original.Name, BaseUrl = original.BaseUrl,
                ApiKey = original.ApiKey, ProfileId = original.ProfileId,
                StreamingFormat = original.StreamingFormat, StreamingBitrateKbps = original.StreamingBitrateKbps
            };
            return (item.Artist, Server: connection,
                IsCurrent: original is null ? (Func<bool>)(() => true) : CreateArtistProfileSourceGuard(original));
        }).ToArray();
        bool CanContinue() => !cancellationToken.IsCancellationRequested &&
            (canContinue?.Invoke() ?? true) && !captured.Any(item =>
                IsArtistImageSuperseded(manualImage, item.Server?.Id, item.Artist.Id, artistName));
        await _artistImageWriteQueue.RunAsync(key, CanContinue, async () =>
        {
            var changed = false;
            try
            {
                var current = new List<(LibraryCatalogArtist Artist, OrynivoServerSettings? Server, Func<bool> IsCurrent)>();
                foreach (var item in captured)
                {
                    if (!CanContinue()) return false;
                    if (!item.IsCurrent()) continue;
                    try
                    {
                        var artist = await ReadCurrentArtworkArtistAsync(item.Artist.Id, item.Server, key, cancellationToken);
                        if (!CanContinue()) return false;
                        if (item.IsCurrent() && artist is not null) current.Add((artist, item.Server, item.IsCurrent));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { /* Unavailable sources cannot block navigation. */ }
                }
                foreach (var source in current.Where(item => HasArtistArtwork((item.Artist, item.Server)))
                    .OrderByDescending(item => item.Artist.ImageIsManual)
                    .ThenByDescending(item => item.Artist.ProfileFetchedAt ?? 0))
                {
                    if (!CanContinue()) return false;
                    if (!source.IsCurrent()) continue;
                    var image = await TryReadArtworkBytesAsync(source.Artist.ArtworkPath, cancellationToken);
                    if (!CanContinue()) return false;
                    if (!source.IsCurrent() || image is null) continue;
                    foreach (var target in current.Where(item => !HasArtistArtwork((item.Artist, item.Server))))
                    {
                        if (!CanContinue() || !source.IsCurrent()) return false;
                        if (!target.IsCurrent()) continue;
                        try
                        {
                            var artist = await ReadCurrentArtworkArtistAsync(target.Artist.Id, target.Server, key, cancellationToken);
                            if (!CanContinue() || !source.IsCurrent()) return false;
                            if (!target.IsCurrent() || artist is null || artist.ImageIsManual ||
                                HasArtistArtwork((artist, target.Server))) continue;
                            if (source.Artist.ImageIsManual)
                            {
                                AdmitArtistImageWrite(target.Server?.Id, artist.Id, artistName);
                                manualImage = _artistImageWrites.CaptureCurrent();
                            }
                            if (target.Server is null)
                            {
                                var path = await ArtistImageSearchService.SaveImageAsync(artist.Id,
                                    image.Value.Data, image.Value.MimeType, cancellationToken);
                                changed = true;
                                await Task.Run(() =>
                                {
                                    using var db = AudioDatabase.OpenDefault();
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (source.Artist.ImageIsManual) db.UpdateArtistImage(artist.Id, path);
                                    else db.UpdateArtistProfile(artist.Id, artist.Biography, path,
                                        artist.SourceUrl, artist.ProfileLanguage ?? GetProfileLanguageCode());
                                }, cancellationToken);
                            }
                            else
                            {
                                var stored = source.Artist.ImageIsManual
                                    ? await _orynivoClient.UploadArtistImageAsync(target.Server, artist.Id,
                                        image.Value.Data, image.Value.MimeType, cancellationToken)
                                    : await _orynivoClient.UpdateArtistProfileAsync(target.Server, artist.Id,
                                        artist.Biography, artist.SourceUrl, artist.ProfileLanguage ?? GetProfileLanguageCode(),
                                        image.Value.Data, image.Value.MimeType, cancellationToken) is not null;
                                changed |= stored;
                                if (stored && target.IsCurrent()) DeleteOrynivoArtistListCache(target.Server);
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch { /* Preserve best-effort synchronization across reachable sources. */ }
                    }
                    return changed;
                }
                return false;
            }
            finally
            {
                if (changed) InvalidateUnifiedLibraryViewCache();
            }
        });
    }

    /// <summary>Reads authoritative artwork and biography state for an unchanged normalized artist.</summary>
    /// <param name="artistId">Source-local artist identifier.</param>
    /// <param name="server">Copied owning connection, or null for local metadata.</param>
    /// <param name="comparisonKey">Original normalized artist identity.</param>
    /// <param name="cancellationToken">Read cancellation.</param>
    /// <returns>Current metadata, or null when missing, unavailable or renamed.</returns>
    private async Task<LibraryCatalogArtist?> ReadCurrentArtworkArtistAsync(long artistId,
        OrynivoServerSettings? server, string comparisonKey, CancellationToken cancellationToken)
    {
        LibraryCatalogArtist? result;
        if (server is null)
        {
            result = await Task.Run(() =>
            {
                using var db = AudioDatabase.OpenDefault();
                cancellationToken.ThrowIfCancellationRequested();
                var artist = db.GetArtistById(artistId);
                return artist is null ? null : new LibraryCatalogArtist(LibraryCatalogSource.Local,
                    artist.Id, artist.Artist, artist.IsFavorite, artist.ImagePath, artist.ImagePath,
                    artist.Biography, artist.SourceUrl, artist.ProfileLanguage, artist.ProfileFetchedAt, artist.ImageIsManual);
            }, cancellationToken);
        }
        else
        {
            var artist = await _orynivoClient.GetArtistAsync(server, artistId, cancellationToken);
            var path = artist?.HasImage == true ? OrynivoServerClient.GetArtistArtworkUrl(server, artistId) : null;
            result = artist is null ? null : new LibraryCatalogArtist(LibraryCatalogSource.OrynivoServer,
                artist.Id, artist.Name, artist.IsFavorite, path, path, artist.Biography,
                artist.SourceUrl, artist.ProfileLanguage, artist.ProfileFetchedAt, artist.ImageIsManual);
        }
        return result is not null && ArtistNameNormalizer.CreateComparisonKey(result.Name) == comparisonKey ? result : null;
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
