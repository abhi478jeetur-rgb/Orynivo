using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using AvaloniaEllipse = Avalonia.Controls.Shapes.Ellipse;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using Orynivo.Audio;
using Orynivo.Controls;
using Orynivo.Library;
using Orynivo.Localization;
using Orynivo.Streaming;
using Windows.Media;

namespace Orynivo;

/// <summary>
/// Remote Orynivo Server album and artist drill-down plus queue item helpers.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Opens every matching local and Orynivo Server album for a remote artist name.</summary>
    /// <param name="artistId">Remote server artist identifier.</param>
    /// <param name="title">Artist display name for the header.</param>
    private Task OpenOrynivoArtistAlbumsAsync(long artistId, string? title) =>
        ShowUnifiedArtistAlbumsAsync(title ?? LocalizationManager.Current.Unknown);

    private readonly NavigationLoadOwnership _remoteAlbumOpens = new();
    private CancellationTokenSource? _remoteAlbumOpenCts;

    /// <summary>Abandons pending remote album metadata and deferred detail restoration.</summary>
    private void CancelRemoteAlbumOpen()
    {
        _remoteAlbumOpens.Invalidate();
        CancelAndDispose(ref _remoteAlbumOpenCts);
    }

    /// <summary>Loads remote metadata and opens detail only while the captured source owns navigation.</summary>
    /// <param name="server">Captured owning server.</param>
    /// <param name="fallback">Captured metadata used when the provider finds no album.</param>
    /// <param name="artistFilterId">Captured provider-local artist scope.</param>
    /// <param name="artistFilterName">Captured artist display name.</param>
    /// <param name="logicalAlbumIds">Optional same-provider album IDs copied before awaiting.</param>
    /// <returns>Current request after detail loading, or null when abandoned.</returns>
    private async Task<NavigationLoadOwnership.Request?> LoadRemoteAlbumDetailAsync(
        OrynivoServerSettings server,
        LibraryCatalogAlbum fallback,
        long? artistFilterId,
        string? artistFilterName,
        IReadOnlyList<long>? logicalAlbumIds = null)
    {
        var tag = _currentTopLevelTag;
        var generation = _unifiedLibraryLoads.Generation;
        var albumIds = logicalAlbumIds?.ToArray();
        var previousCancellation = _remoteAlbumOpenCts;
        _remoteAlbumOpenCts = new CancellationTokenSource();
        var token = _remoteAlbumOpenCts.Token;
        var request = _remoteAlbumOpens.Begin(token);
        CancelAndDispose(ref previousCancellation);
        bool CanPublish() => _remoteAlbumOpens.IsCurrent(request) &&
            generation == _unifiedLibraryLoads.Generation &&
            ReferenceEquals(_activeOrynivoServer, server) &&
            string.Equals(_currentTopLevelTag, tag, StringComparison.Ordinal) &&
            (_settings.OrynivoServers ?? []).Any(candidate => ReferenceEquals(candidate, server));
        if (!CanPublish()) return null;
        try
        {
            var provider = CreateOrynivoCatalogProvider(server);
            var album = await provider.GetAlbumAsync(fallback.Id, includeArtwork: true, cancellationToken: token);
            if (!CanPublish()) return null;
            await ShowProviderAlbumTracksAsync(provider, album ?? fallback,
                artistFilterId, artistFilterName, albumIds);
            return CanPublish() ? request : null;
        }
        catch (Exception) when (!CanPublish())
        {
            return null;
        }
    }

    /// <summary>Opens remote album tracks with source and artist scope captured before metadata loading.</summary>
    /// <param name="albumId">Remote album identifier.</param>
    /// <param name="title">Fallback album title.</param>
    /// <param name="artist">Fallback album artist.</param>
    /// <param name="artistFilterId">Optional provider-local artist scope.</param>
    /// <param name="artistFilterName">Optional artist display scope.</param>
    /// <returns>A task completing after detail loading or abandonment.</returns>
    private async Task OpenOrynivoAlbumTracksAsync(
        long albumId,
        string? title,
        string? artist,
        long? artistFilterId = null,
        string? artistFilterName = null)
    {
        if (!_navigationRestores.TryAdmitNavigation()) return;
        if (_activeOrynivoServer is not { } activeServer) return;
        var server = (_settings.OrynivoServers ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, activeServer.Id, StringComparison.Ordinal));
        if (server is null) return;
        _activeOrynivoServer = server;
        var scopeId = artistFilterId ?? _activeArtistFilterId;
        var scopeName = artistFilterName ?? _activeArtistFilterName;
        var fallback = new LibraryCatalogAlbum(LibraryCatalogSource.OrynivoServer,
            albumId, title ?? LocalizationManager.Current.Unknown, artist, null, null, null,
            IsOrynivoFavorite(server, "Album", albumId));
        PushCurrentNavigationState();
        _orynivoNavigationStack.Push((_activeOrynivoView, null, null));
        BackButton.IsVisible = true;
        await LoadRemoteAlbumDetailAsync(server, fallback, scopeId, scopeName);
    }

    /// <summary>Opens remote row detail with fallback metadata and scope captured before awaiting.</summary>
    /// <param name="row">Selected remote album row.</param>
    /// <returns>A task completing after detail loading or abandonment.</returns>
    private async Task OpenOrynivoAlbumTracksAsync(ContentRow row)
    {
        if (!_navigationRestores.TryAdmitNavigation()) return;
        if (row.Id is not long albumId) return;
        var source = row.OrynivoServer ?? _activeOrynivoServer;
        if (source is null || !string.Equals(_activeOrynivoServer?.Id, source.Id, StringComparison.Ordinal)) return;
        var server = (_settings.OrynivoServers ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, source.Id, StringComparison.Ordinal));
        if (server is null) return;
        _activeOrynivoServer = server;
        var (artistFilterId, artistFilterName) = GetAlbumArtistScope(row);
        var albumIds = row.LogicalAlbumIds?.ToArray();
        var fallback = new LibraryCatalogAlbum(LibraryCatalogSource.OrynivoServer,
            albumId, row.Title ?? LocalizationManager.Current.Unknown, row.Artist,
            int.TryParse(row.Year, NumberStyles.Integer, CultureInfo.CurrentCulture, out var year) ? year : null,
            row.ArtworkPath, row.ThumbnailPath, row.IsFavorite, row.ArtistId);
        PushCurrentNavigationState();
        _orynivoNavigationStack.Push((_activeOrynivoView, null, null));
        BackButton.IsVisible = true;
        await LoadRemoteAlbumDetailAsync(server, fallback, artistFilterId, artistFilterName, albumIds);
    }

    /// <summary>Deletes the cached remote album list after album artwork metadata changes.</summary>
    /// <param name="server">Server whose album list cache should be removed.</param>
    internal static void DeleteOrynivoAlbumListCache(OrynivoServerSettings server)
    {
        try
        {
            var path = GetOrynivoAlbumListCachePath(server);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Resolves the provider-local artist scope to retain when an album is opened
    /// from the unified artist album list.
    /// </summary>
    /// <param name="row">The selected album row.</param>
    /// <returns>
    /// The artist identifier and display name used to initially filter the album,
    /// or the ambient scope when the album was opened from another view.
    /// </returns>
    private (long? ArtistId, string? ArtistName) GetAlbumArtistScope(ContentRow row)
    {
        var isUnifiedArtistAlbumList =
            _activeAlbumFilterId is null &&
            _activeArtistFilterId is null &&
            !string.IsNullOrWhiteSpace(_activeArtistFilterName) &&
            (string.Equals(_currentTopLevelTag, "Albums", StringComparison.Ordinal) ||
             string.Equals(_currentTopLevelTag, "UnifiedArtistAlbums", StringComparison.Ordinal));

        return isUnifiedArtistAlbumList
            ? (row.ArtistId, _activeArtistFilterName)
            : (_activeArtistFilterId, _activeArtistFilterName);
    }

    /// <summary>Restores a source-bound album only while its overview and metadata requests remain current.</summary>
    /// <param name="navigationTag">Source-bound remote navigation tag.</param>
    /// <param name="albumId">Owning server's album identifier.</param>
    /// <param name="albumTitle">Saved album title.</param>
    /// <param name="artistFilterId">Optional provider-local artist scope.</param>
    /// <param name="artistFilterName">Optional scoped artist display name.</param>
    /// <param name="verticalOffset">Saved scroll offset.</param>
    /// <param name="logicalAlbumIds">Optional album identifiers from the same provider.</param>
    /// <returns>A task completing after restoration or rejection of an unavailable source.</returns>
    private async Task RestoreOrynivoAlbumTracksAsync(
        string? navigationTag,
        long albumId,
        string? albumTitle,
        long? artistFilterId,
        string? artistFilterName,
        double? verticalOffset,
        IReadOnlyList<long>? logicalAlbumIds = null)
    {
        if (!ServerNavigationTarget.TryParse(navigationTag, out var target))
            return;
        var server = (_settings.OrynivoServers ?? [])
            .FirstOrDefault(candidate => string.Equals(candidate.Id, target.ServerId, StringComparison.Ordinal));
        if (server is null) return;
        var restoreRequest = _navigationRestores.Context;
        bool CanRestore() => (restoreRequest is null || _navigationRestores.IsCurrent(restoreRequest)) &&
            ReferenceEquals(_activeOrynivoServer, server) &&
            string.Equals(_currentTopLevelTag, navigationTag, StringComparison.Ordinal) &&
            (_settings.OrynivoServers ?? []).Any(candidate => ReferenceEquals(candidate, server));

        var generation = _unifiedLibraryLoads.Generation;
        var albumIds = logicalAlbumIds?.ToArray();
        var fallback = new LibraryCatalogAlbum(LibraryCatalogSource.OrynivoServer,
            albumId, albumTitle ?? LocalizationManager.Current.Unknown, null, null, null, null,
            IsOrynivoFavorite(server, "Album", albumId));
        SelectNavigationItem(navigationTag!);
        var overviewToken = new CancellationToken(canceled: true);
        var overview = LoadTopLevelViewAsync(navigationTag!, token => overviewToken = token);
        await overview;
        if (overviewToken.IsCancellationRequested || generation != _unifiedLibraryLoads.Generation || !CanRestore()) return;

        var request = await LoadRemoteAlbumDetailAsync(server, fallback,
            artistFilterId, artistFilterName, albumIds);
        if (request is null) return;
        bool CanRestoreDetail() => _remoteAlbumOpens.IsCurrent(request) &&
            generation == _unifiedLibraryLoads.Generation && CanRestore();
        RestoreSelectionFromCurrentItems(null, verticalOffset, canRestore: CanRestoreDetail);
    }

    /// <summary>Restores a remote artist list while its Back execution, overview and filtered load remain current.</summary>
    /// <param name="navigationTag">Remote server navigation tag to restore.</param>
    /// <param name="artistId">Remote server artist identifier.</param>
    /// <param name="artistName">Artist display name for the header.</param>
    /// <param name="selectedAlbumId">Album identifier to reselect.</param>
    /// <param name="verticalOffset">Optional vertical scroll offset to restore.</param>
    /// <returns>A task completing after restoration or rejection of an unavailable source.</returns>
    private async Task RestoreOrynivoArtistAlbumsAsync(
        string? navigationTag,
        long artistId,
        string? artistName,
        long? selectedAlbumId,
        double? verticalOffset)
    {
        if (!ServerNavigationTarget.TryParse(navigationTag, out var target))
            return;
        var server = (_settings.OrynivoServers ?? [])
            .FirstOrDefault(candidate => string.Equals(candidate.Id, target.ServerId, StringComparison.Ordinal));
        if (server is null) return;
        var restoreRequest = _navigationRestores.Context;
        bool CanRestore() => (restoreRequest is null || _navigationRestores.IsCurrent(restoreRequest)) &&
            ReferenceEquals(_activeOrynivoServer, server) &&
            string.Equals(_currentTopLevelTag, navigationTag, StringComparison.Ordinal) &&
            (_settings.OrynivoServers ?? []).Any(candidate => ReferenceEquals(candidate, server));

        var generation = _unifiedLibraryLoads.Generation;
        SelectNavigationItem(navigationTag!);
        var overviewToken = new CancellationToken(canceled: true);
        await LoadTopLevelViewAsync(navigationTag!, admitted => overviewToken = admitted);
        if (overviewToken.IsCancellationRequested || generation != _unifiedLibraryLoads.Generation || !CanRestore()) return;

        _currentTopLevelTag = navigationTag!;
        _activeOrynivoView = "Albums";
        _activeArtistFilterId = artistId;
        _activeArtistFilterName = artistName;
        ContentTitleTextBlock.Text = $"{server.Name} · {artistName}";
        var detail = LoadOrynivoViewAsync(filterArtistId: artistId);
        var detailToken = _orynivoViewCts?.Token;
        await detail;
        RestoreSelectionFromCurrentItems(selectedAlbumId, verticalOffset,
            canRestore: () => CanRestore() && detailToken.HasValue && !detailToken.Value.IsCancellationRequested &&
                generation == _unifiedLibraryLoads.Generation);
    }

    /// <summary>Queues playable rows and starts the selected occurrence, preserving playlist duplicates.</summary>
    /// <param name="row">Selected row occurrence.</param>
    /// <param name="allRows">Ordered context rows, including any non-playable placeholders.</param>
    /// <returns>A task completing after playback starts or reports its failure.</returns>
    private async Task PlayTrackFromRowsAsync(ContentRow row, List<ContentRow> allRows)
    {
        if (string.IsNullOrEmpty(row.FilePath))
            return;

        var playableRows = allRows.Where(r => !string.IsNullOrEmpty(r.FilePath)).ToList();
        var selectedIndex = PlaylistRowPlaybackIndex.Find(row, playableRows);
        if (selectedIndex < 0) return;
        StopInfiniteMix();
        _queueEdits.Invalidate();
        _queue.Clear();
        foreach (var r in playableRows)
            _queue.Add(ToPlaylistItem(r));

        _queueIndex = selectedIndex;
        ResetQueuePlaybackState();
        PersistPlaybackQueue();
        RefreshQueueNavigationButtons();

        try { await StartPlaybackAsync(row.FilePath); }
        catch (OperationCanceledException) { StatusTextBlock.Text = LocalizationManager.Current.PlaybackStopped; }
        catch (Exception ex) { StopPlayback(); StatusTextBlock.Text = ex.Message; }
    }

    private static PlaylistItem ToPlaylistItem(ContentRow row) =>
        new(
            row.FilePath,
            string.IsNullOrWhiteSpace(row.Title) ? null : row.Title,
            string.IsNullOrWhiteSpace(row.Artist) ? null : row.Artist,
            string.IsNullOrWhiteSpace(row.Album) ? null : row.Album,
            string.IsNullOrWhiteSpace(row.Duration) ? null : row.Duration,
            string.IsNullOrWhiteSpace(row.Format) ? null : row.Format,
            row.KnownDuration);

    private PlaylistItem CreatePlaylistItem(string path)
    {
        if (_orynivoTracksByUrl.TryGetValue(path, out var orynivoRow))
            return ToPlaylistItem(orynivoRow);
        if (_plexTracksByUrl.TryGetValue(path, out var plexRow))
            return ToPlaylistItem(plexRow);
        // Never perform a synchronous provider/network lookup while building a
        // queue. Missing metadata is optional; playback resolves the source
        // asynchronously and the queue item can be enriched later.
        return new PlaylistItem(path);
    }

    private PlaylistItem? GetPlaylistMetadata(string path)
    {
        var queueItem = _queue.FirstOrDefault(item =>
            string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (queueItem is not null &&
            (!string.IsNullOrWhiteSpace(queueItem.Title) ||
             !string.IsNullOrWhiteSpace(queueItem.Artist) ||
             !string.IsNullOrWhiteSpace(queueItem.Album)))
        {
            return queueItem;
        }

        if (_orynivoTracksByUrl.TryGetValue(path, out var orynivoRow))
            return ToPlaylistItem(orynivoRow);
        if (TryResolveOrynivoPlaylistReferenceRow(path, out var referencedRow))
            return ToPlaylistItem(referencedRow);
        if (_plexTracksByUrl.TryGetValue(path, out var plexRow))
            return ToPlaylistItem(plexRow);
        return null;
    }

    private bool TryResolveOrynivoPlaylistReferenceRow(string path, out ContentRow row)
    {
        row = null!;
        if (!TryResolveOrynivoPlaylistReference(path, out var server, out var trackId))
            return false;

        try
        {
            var provider = CreateOrynivoCatalogProvider(server);
            var remoteTrack = provider.GetTracksByIdsAsync([trackId])
                .GetAwaiter()
                .GetResult()
                .FirstOrDefault();
            if (remoteTrack is null)
                return false;
            row = ToCatalogTrackContentRow(remoteTrack, server);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
