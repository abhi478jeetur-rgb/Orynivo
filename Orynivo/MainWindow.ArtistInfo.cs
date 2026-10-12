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
/// Artist detail surface (biography, image actions, and rename/merge), unified
/// artist album and track population, and now-playing detail-view handling for
/// <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow : Window
{
    private async void ArtistInfoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ArtistInfoCloseButton.IsVisible = true;
        if (_currentPodcastPlayback is { } podcastPlayback)
        {
            LyricsView.IsVisible = false;
            ArtistInfoView.IsVisible = false;
            PodcastInfoView.IsVisible = !(PodcastInfoView.IsVisible);
            if (PodcastInfoView.IsVisible)
                ShowPodcastInfo(podcastPlayback);
            UpdateBackButtonForDetailView();
            return;
        }

        if (_currentOrynivoTrackRow is { ArtistId: not null } &&
            _currentNowPlayingProvider is OrynivoServerNowPlayingMetadataProvider)
        {
            _artistInfoUnifiedArtistName = null;
            LyricsView.IsVisible = false;
            PodcastInfoView.IsVisible = false;
            ArtistInfoView.IsVisible = !ArtistInfoView.IsVisible;
            UpdateBackButtonForDetailView();
            if (ArtistInfoView.IsVisible)
                await ShowNowPlayingRemoteArtistInfoAsync(forceRefresh: false);
            return;
        }

        if (_currentArtistId is not long artistId || string.IsNullOrWhiteSpace(_currentArtistName))
            return;

        LyricsView.IsVisible = false;
        PodcastInfoView.IsVisible = false;
        _artistInfoUnifiedArtistName = null;
        ArtistInfoView.IsVisible = !(ArtistInfoView.IsVisible);
        UpdateBackButtonForDetailView();
        if (ArtistInfoView.IsVisible)
            await ShowArtistInfoAsync(artistId, forceRefresh: false);
    }

    private void CloseArtistInfoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_artistInfoUnifiedArtistName))
        {
            BackButton_OnClick(sender, e);
            return;
        }

        CloseNowPlayingDetailViews();
    }

    private void ClosePodcastInfoButton_OnClick(object? sender, RoutedEventArgs e)
        => CloseNowPlayingDetailViews();

    private async void ArtistInfoTitleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(_artistInfoUnifiedArtistName))
            return;
        if (_artistInfoDisplayedRemoteRow is { } remoteRow)
        {
            CloseNowPlayingDetailViews();
            await HandleContentRowDoubleClickAsync(remoteRow);
            return;
        }

        if (_artistInfoDisplayedId is not long artistId)
            return;

        var artistName = ArtistInfoTitleButton.Content as string;
        CloseNowPlayingDetailViews();
        await ShowArtistAlbumsAsync(
            artistId,
            artistName ?? LocalizationManager.Current.Unknown);
    }

    private async void ArtistInfoListButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContentRow row })
            return;

        e.Handled = true;
        if (row.EntityType == "UnifiedArtist")
        {
            await ShowUnifiedArtistAlbumsAsync(row.Title ?? LocalizationManager.Current.Unknown);
            return;
        }

        var artistId = row.EntityType switch
        {
            "Artist" or "OrynivoArtist" => row.Id,
            _ => row.ArtistId
        };

        if (artistId is null)
        {
            if (row.EntityType == "Album" && (row.AlbumId ?? row.Id) is long albumId)
            {
                using var db = AudioDatabase.OpenDefault();
                artistId = db.GetAlbumArtistId(albumId);
            }
            else if (!string.IsNullOrWhiteSpace(row.FilePath))
            {
                using var db = AudioDatabase.OpenDefault();
                artistId = db.GetTrackNavigationIds(row.FilePath).ArtistId;
            }
            row.ArtistId = artistId;
        }

        if (artistId is not long id)
            return;

        var artistName = row.EntityType is "Artist" or "OrynivoArtist"
            ? row.Title
            : row.Artist;
        await ShowUnifiedArtistAlbumsAsync(artistName ?? LocalizationManager.Current.Unknown);
    }

    private void UpdateBackButtonForDetailView()
    {
        BackButton.IsVisible =
            LyricsView.IsVisible ||
            ArtistInfoView.IsVisible ||
            PodcastInfoView.IsVisible ||
            _plexNavigationStack.Count > 0 ||
            _navigationStack.Count > 0;
    }

    private void CloseNowPlayingDetailViews()
    {
        LyricsView.IsVisible = false;
        ArtistInfoView.IsVisible = false;
        PodcastInfoView.IsVisible = false;
        BackButton.IsVisible = _plexNavigationStack.Count > 0 || _navigationStack.Count > 0;
    }

    private void ResetArtistInfoSurface(string artistName)
    {
        _artistInfoDisplayedId = null;
        _artistInfoDisplayedRemoteRow = null;
        _nowPlayingRemoteArtistInfo = false;
        _artistInfoSourceUrl = null;
        ArtistInfoTitleButton.Content = artistName;
        ArtistInfoImage.Source = null;
        ArtistInfoImagePlaceholder.IsVisible = true;
        ArtistInfoBiographyTextBlock.Text = string.Empty;
        ArtistInfoSourceButton.Content = LocalizationManager.Current.ArtistInfoSource;
        ArtistInfoSourceButton.IsVisible = false;
        ArtistInfoImageStatusText.Text = string.Empty;
        ArtistInfoImageStatusText.IsVisible = false;
        SetArtistInfoFavoriteState(false);
        if (string.IsNullOrWhiteSpace(_artistInfoUnifiedArtistName))
            ResetArtistInfoAlbums();
    }

    /// <summary>Collects a profile lookup name and refreshes only the original displayed artist.</summary>
    /// <param name="sender">The refresh button.</param>
    /// <param name="e">The routed click event.</param>
    private async void RefreshArtistInfoButton_OnClick(object? sender, RoutedEventArgs e)
        => await RefreshDisplayedArtistProfileAsync(async artistName =>
        {
            var dialog = new ArtistProfileSearchDialog(artistName);
            return await dialog.ShowDialog<bool>(this) ? dialog.Query : null;
        });

    /// <summary>Retains the displayed profile identity across lookup selection without replacing its album strip.</summary>
    /// <param name="selectQuery">Collects the confirmed lookup name, or <see langword="null"/> for cancellation.</param>
    /// <returns>A task completing selection and the still-admitted profile refresh.</returns>
    private async Task RefreshDisplayedArtistProfileAsync(Func<string, Task<string?>> selectQuery)
    {
        var artistName = ArtistInfoTitleButton.Content as string;
        if (string.IsNullOrWhiteSpace(artistName)) return;
        var localId = _artistInfoDisplayedId;
        var remoteRow = _artistInfoDisplayedRemoteRow;
        var nowPlaying = _nowPlayingRemoteArtistInfo;
        var trackRow = _currentOrynivoTrackRow;
        var provider = _currentNowPlayingProvider;
        var playingArtistId = trackRow?.ArtistId;
        var server = nowPlaying ? trackRow?.OrynivoServer : remoteRow?.OrynivoServer ?? _activeOrynivoServer;
        var sourceIsCurrent = nowPlaying || remoteRow is not null
            ? CreateArtistProfileSourceGuard(server)
            : () => true;
        var tag = _currentTopLevelTag;
        var detailVersion = _artistDetailLoadVersion;
        var unifiedName = _artistInfoUnifiedArtistName;
        var observation = _artistProfileLoads.CaptureCurrent();
        var restore = _navigationRestores.Context;
        bool CanRefresh() => ArtistInfoView.IsVisible &&
            _artistProfileLoads.IsCurrent(observation) && _currentTopLevelTag == tag &&
            _artistDetailLoadVersion == detailVersion && _artistInfoUnifiedArtistName == unifiedName &&
            ArtistInfoTitleButton.Content as string == artistName && _artistInfoDisplayedId == localId &&
            ReferenceEquals(_artistInfoDisplayedRemoteRow, remoteRow) &&
            _nowPlayingRemoteArtistInfo == nowPlaying && sourceIsCurrent() &&
            (!nowPlaying || (ReferenceEquals(_currentOrynivoTrackRow, trackRow) &&
                ReferenceEquals(_currentNowPlayingProvider, provider) &&
                trackRow?.ArtistId == playingArtistId && ReferenceEquals(trackRow?.OrynivoServer, server))) &&
            (remoteRow is null || ReferenceEquals(remoteRow.OrynivoServer ?? _activeOrynivoServer, server)) &&
            (restore is null || _navigationRestores.IsCurrent(restore));
        if (!CanRefresh() || (!nowPlaying && remoteRow is null && localId is null)) return;
        var query = await selectQuery(artistName);
        if (!CanRefresh() || string.IsNullOrWhiteSpace(query)) return;

        if (nowPlaying)
            await ShowNowPlayingRemoteArtistInfoAsync(forceRefresh: true, query);
        else if (remoteRow is not null)
            await ShowOrynivoArtistInfoAsync(remoteRow, forceRefresh: true, query);
        else if (localId is long artistId)
            await ShowArtistInfoAsync(artistId, forceRefresh: true, query);
    }

    /// <summary>Searches artwork while retaining the originally displayed artist.</summary>
    /// <param name="sender">The search button.</param>
    /// <param name="e">The routed click event.</param>
    private async void SearchArtistImageButton_OnClick(object? sender, RoutedEventArgs e)
        => await SearchDisplayedArtistImageAsync(async artistName =>
        {
            var dialog = new ArtistImageSearchWindow(artistName, _settings.FanartTvApiKey);
            return await dialog.ShowDialog<bool>(this) ? dialog.SelectedResult : null;
        });

    /// <summary>Adapts a confirmed search result to the guarded manual-image publication flow.</summary>
    /// <param name="selectImage">Searches the original artist name and returns accepted bytes, or null.</param>
    /// <returns>A task completing selection and the still-admitted image operation.</returns>
    private Task SearchDisplayedArtistImageAsync(Func<string, Task<ArtistImageDownload?>> selectImage)
    {
        if (!TryGetDisplayedArtistName(out var artistName)) return Task.CompletedTask;
        return ApplyDisplayedArtistImageAsync(async () =>
        {
            var selected = await selectImage(artistName);
            return selected is null ? null : (selected.ImageData, selected.MimeType ?? "image/jpeg");
        }, requireOwningServerUpload: true);
    }

    /// <summary>Owns manual image publication independently from profile observations.</summary>
    private readonly NavigationLoadOwnership _artistImageActions = new();

    /// <summary>Captures the displayed artist, source and playback context for a manual image action.</summary>
    /// <returns>A predicate rejecting replacement image actions, profiles, sources or navigation.</returns>
    private Func<bool> CaptureArtistImageActionGuard()
    {
        var request = _artistImageActions.Begin(CancellationToken.None);
        var profile = _artistProfileLoads.CaptureCurrent();
        var title = ArtistInfoTitleButton.Content as string;
        var localId = _artistInfoDisplayedId;
        var row = _artistInfoDisplayedRemoteRow;
        var rowId = row?.Id;
        var rowTitle = row?.Title;
        var nowPlaying = _nowPlayingRemoteArtistInfo;
        var track = _currentOrynivoTrackRow;
        var provider = _currentNowPlayingProvider;
        var playingArtistId = track?.ArtistId;
        var server = nowPlaying ? track?.OrynivoServer : row?.OrynivoServer ?? _activeOrynivoServer;
        var sourceIsCurrent = nowPlaying || row is not null ? CreateArtistProfileSourceGuard(server) : () => true;
        var tag = _currentTopLevelTag;
        var detailVersion = _artistDetailLoadVersion;
        var unifiedName = _artistInfoUnifiedArtistName;
        var restore = _navigationRestores.Context;
        return () => ArtistInfoView.IsVisible && _artistImageActions.IsCurrent(request) &&
            _artistProfileLoads.IsCurrent(profile) && ArtistInfoTitleButton.Content as string == title &&
            _artistInfoDisplayedId == localId && ReferenceEquals(_artistInfoDisplayedRemoteRow, row) &&
            row?.Id == rowId && row?.Title == rowTitle && _nowPlayingRemoteArtistInfo == nowPlaying &&
            _currentTopLevelTag == tag && _artistDetailLoadVersion == detailVersion &&
            _artistInfoUnifiedArtistName == unifiedName && sourceIsCurrent() &&
            (row is null || ReferenceEquals(row.OrynivoServer ?? _activeOrynivoServer, server)) &&
            (!nowPlaying || (ReferenceEquals(_currentOrynivoTrackRow, track) &&
                ReferenceEquals(_currentNowPlayingProvider, provider) && track?.ArtistId == playingArtistId &&
                ReferenceEquals(track?.OrynivoServer, server))) &&
            (nowPlaying || row is not null || localId is not null) &&
            (restore is null || _navigationRestores.IsCurrent(restore));
    }

    /// <summary>Selects and uploads an image only for the original displayed artist.</summary>
    /// <param name="sender">The upload button.</param>
    /// <param name="e">The routed click event.</param>
    private async void UploadArtistImageButton_OnClick(object? sender, RoutedEventArgs e)
        => await UploadDisplayedArtistImageAsync(PickImageAsync);

    /// <summary>Retains the original upload target through selection, synchronization and decoding.</summary>
    /// <param name="pickImage">Selects image bytes and MIME type, or null for cancellation.</param>
    /// <returns>A task completing accepted synchronization and still-current image publication.</returns>
    private Task UploadDisplayedArtistImageAsync(Func<Task<(byte[] Data, string MimeType)?>> pickImage)
        => ApplyDisplayedArtistImageAsync(pickImage, requireOwningServerUpload: false);

    /// <summary>Retains selection, owning-server upload and synchronized manual-image publication.</summary>
    /// <param name="pickImage">Selects accepted bytes, or null for cancellation.</param>
    /// <param name="requireOwningServerUpload">Whether search must first upload directly to the captured owning server.</param>
    /// <returns>A task completing accepted data work and still-current image publication.</returns>
    private async Task ApplyDisplayedArtistImageAsync(Func<Task<(byte[] Data, string MimeType)?>> pickImage,
        bool requireOwningServerUpload)
    {
        var canPublish = CaptureArtistImageActionGuard();
        var remoteRow = _artistInfoDisplayedRemoteRow;
        var nowPlaying = _nowPlayingRemoteArtistInfo;
        var server = nowPlaying ? _currentOrynivoTrackRow?.OrynivoServer : remoteRow?.OrynivoServer ?? _activeOrynivoServer;
        var owningArtistId = nowPlaying ? _currentOrynivoTrackRow?.ArtistId : remoteRow?.Id;
        var sourceIsCurrent = CreateArtistProfileSourceGuard(server);
        var connection = server is null ? null : new OrynivoServerSettings
        {
            Id = server.Id, Name = server.Name, BaseUrl = server.BaseUrl,
            ApiKey = server.ApiKey, ProfileId = server.ProfileId,
            StreamingFormat = server.StreamingFormat, StreamingBitrateKbps = server.StreamingBitrateKbps
        };
        var localId = _artistInfoDisplayedId;
        if (!canPublish() || !TryGetDisplayedArtistName(out var artistName)) return;
        try
        {
            var image = await pickImage();
            if (!canPublish() || image is null) return;
            if (requireOwningServerUpload && connection is not null && owningArtistId is long remoteArtistId)
            {
                var uploaded = await _orynivoClient.UploadArtistImageAsync(
                    connection, remoteArtistId, image.Value.Data, image.Value.MimeType);
                if (!uploaded)
                {
                    if (canPublish())
                    {
                        ArtistInfoImageStatusText.Text = LocalizationManager.Current.ArtistImageDownloadFailed;
                        ArtistInfoImageStatusText.IsVisible = true;
                    }
                    return;
                }
                InvalidateUnifiedLibraryViewCache();
                if (sourceIsCurrent()) DeleteOrynivoArtistListCache(connection);
                if (!canPublish()) return;
            }
            await SynchronizeUnifiedArtistImageAsync(artistName, image.Value.Data, image.Value.MimeType);
            if (!canPublish()) return;
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(image.Value.Data);
                return new Bitmap(stream);
            });
            if (!canPublish()) { bitmap.Dispose(); return; }
            ArtistInfoImage.Source = bitmap;
            ArtistInfoImagePlaceholder.IsVisible = false;
            ArtistInfoImageStatusText.IsVisible = false;
            if (remoteRow is not null && server is not null && remoteRow.Id is long artistId)
            {
                EnsureOrynivoArtistArtworkPaths(server, artistId, remoteRow);
                InvalidateRemoteArtworkCache(remoteRow.ArtworkPath);
                InvalidateRemoteArtworkCache(remoteRow.ThumbnailPath);
                WriteRemoteArtworkCache(remoteRow.ArtworkPath, image.Value.Data);
                WriteRemoteArtworkCache(remoteRow.ThumbnailPath, image.Value.Data);
                remoteRow.Artwork = bitmap;
                remoteRow.Thumbnail = bitmap;
                remoteRow.ArtworkLoadQueued = remoteRow.ThumbnailLoadQueued = false;
                remoteRow.ArtworkLoadCompleted = remoteRow.ThumbnailLoadCompleted = true;
                remoteRow.ImageIsManual = true;
            }
            else if (requireOwningServerUpload && nowPlaying && server is not null && owningArtistId is long playingArtistId)
            {
                var imageUrl = OrynivoServerClient.GetArtistArtworkUrl(server, playingArtistId);
                InvalidateRemoteArtworkCache(imageUrl);
                WriteRemoteArtworkCache(imageUrl, image.Value.Data);
            }
            else if (localId is long localArtistId)
            {
                var artist = await Task.Run(() =>
                {
                    using var db = AudioDatabase.OpenDefault();
                    return db.GetArtistById(localArtistId);
                });
                if (!canPublish()) return;
                if (artist is not null) await RefreshVisibleArtistRowAsync(artist, canPublish);
            }
            if (canPublish()) StatusTextBlock.Text = string.Empty;
        }
        catch
        {
            if (!canPublish()) return;
            ArtistInfoImageStatusText.Text = LocalizationManager.Current.ArtistImageDownloadFailed;
            ArtistInfoImageStatusText.IsVisible = true;
        }
    }

    /// <summary>Deletes the original displayed artist's images without clearing a replacement view.</summary>
    /// <param name="sender">The delete button.</param>
    /// <param name="e">The routed click event.</param>
    private async void DeleteArtistImageButton_OnClick(object? sender, RoutedEventArgs e)
        => await DeleteDisplayedArtistImageAsync();

    /// <summary>Retains deletion publication across synchronization and local row refresh.</summary>
    /// <returns>A task completing accepted deletion and still-current image cleanup.</returns>
    private async Task DeleteDisplayedArtistImageAsync()
    {
        var canPublish = CaptureArtistImageActionGuard();
        var remoteRow = _artistInfoDisplayedRemoteRow;
        var localId = _artistInfoDisplayedId;
        if (!canPublish() || !TryGetDisplayedArtistName(out var artistName)) return;
        try
        {
            await SynchronizeUnifiedArtistImageDeletionAsync(artistName);
            if (!canPublish()) return;
            ArtistInfoImage.Source = null;
            ArtistInfoImagePlaceholder.IsVisible = true;
            ArtistInfoImageStatusText.IsVisible = false;
            if (remoteRow is not null)
            {
                InvalidateRemoteArtworkCache(remoteRow.ArtworkPath);
                InvalidateRemoteArtworkCache(remoteRow.ThumbnailPath);
                remoteRow.ArtworkPath = remoteRow.ThumbnailPath = null;
                UpdateRowArtworkFromBytes(remoteRow, null);
                remoteRow.ImageIsManual = false;
            }
            else if (localId is long artistId)
            {
                var artist = await Task.Run(() =>
                {
                    using var db = AudioDatabase.OpenDefault();
                    return db.GetArtistById(artistId);
                });
                if (!canPublish()) return;
                if (artist is not null) await RefreshVisibleArtistRowAsync(artist, canPublish);
            }
            if (canPublish()) StatusTextBlock.Text = string.Empty;
        }
        catch
        {
            if (!canPublish()) return;
            ArtistInfoImageStatusText.Text = LocalizationManager.Current.ArtistImageDownloadFailed;
            ArtistInfoImageStatusText.IsVisible = true;
        }
    }

    /// <summary>Resolves the artist currently displayed in the artist-information surface.</summary>
    /// <param name="artistName">Receives the artist display name.</param>
    /// <returns><see langword="true"/> when an artist context is available.</returns>
    private bool TryGetDisplayedArtistName(out string artistName)
    {
        artistName = ArtistInfoTitleButton.Content as string ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(artistName))
            return true;

        if (_currentOrynivoTrackRow is { Artist: { } nowPlayingArtist })
        {
            artistName = nowPlayingArtist;
            return !string.IsNullOrWhiteSpace(artistName);
        }

        return false;
    }

    /// <summary>Shows or hides the complete manual artist-image action group.</summary>
    /// <param name="isVisible">Whether image search, upload, and deletion are available.</param>
    private void SetArtistImageActionsVisible(bool isVisible)
    {
        SearchArtistImageButton.IsVisible = isVisible;
        UploadArtistImageButton.IsVisible = isVisible;
        DeleteArtistImageButton.IsVisible = isVisible;
    }

    /// <summary>Deletes matching artist images while retaining the original server set and connection identity.</summary>
    /// <param name="artistName">Artist display name used for normalized identity matching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the synchronized deletion.</returns>
    private async Task SynchronizeUnifiedArtistImageDeletionAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        var comparisonKey = ArtistNameNormalizer.CreateComparisonKey(artistName);
        if (comparisonKey.Length == 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var sources = (_settings.OrynivoServers ?? []).Select(server => (
            Server: new OrynivoServerSettings
            {
                Id = server.Id, Name = server.Name, BaseUrl = server.BaseUrl,
                ApiKey = server.ApiKey, ProfileId = server.ProfileId,
                StreamingFormat = server.StreamingFormat,
                StreamingBitrateKbps = server.StreamingBitrateKbps
            },
            IsCurrent: CreateArtistProfileSourceGuard(server))).ToArray();
        try
        {
            await Task.Run(() =>
            {
                using var db = AudioDatabase.OpenDefault();
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var artist in db.GetArtistsLite().Where(candidate =>
                             ArtistNameNormalizer.CreateComparisonKey(candidate.Artist) == comparisonKey))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (db.ClearArtistImage(artist.Id))
                        ArtistImageSearchService.DeleteImage(artist.Id);
                }
            }, cancellationToken);

            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!source.IsCurrent()) continue;
                var server = source.Server;
                try
                {
                    var artists = await _orynivoClient.GetArtistsAsync(server, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!source.IsCurrent()) continue;
                    foreach (var artist in artists.Where(candidate =>
                                 ArtistNameNormalizer.CreateComparisonKey(candidate.Name) == comparisonKey))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!source.IsCurrent()) break;
                        await _orynivoClient.DeleteArtistImageAsync(server, artist.Id, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!source.IsCurrent()) break;
                        var imageUrl = OrynivoServerClient.GetArtistArtworkUrl(server, artist.Id);
                        InvalidateRemoteArtworkCache(imageUrl);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (source.IsCurrent()) DeleteOrynivoArtistListCache(server);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Deletion remains applied to every reachable matching library.
                }
            }
        }
        finally
        {
            // Admitted writes may complete before cancellation; never retain stale catalog snapshots.
            InvalidateUnifiedLibraryViewCache();
        }
    }
}
