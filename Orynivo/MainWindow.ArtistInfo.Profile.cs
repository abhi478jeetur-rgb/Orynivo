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
/// Artist detail surfaces, biography/profile loading, and now-playing artist info.
/// </summary>
public partial class MainWindow : Window
{
    private readonly NavigationLoadOwnership _artistProfileLoads = new();

    /// <summary>Loads a local artist profile while retaining navigation and decode publication ownership.</summary>
    /// <param name="artistId">Local artist identifier.</param>
    /// <param name="forceRefresh">Whether an external profile refresh is required.</param>
    /// <param name="profileLookupName">Optional external lookup name.</param>
    /// <returns>A task completing after profile publication or abandonment.</returns>
    private async Task ShowArtistInfoAsync(
        long artistId,
        bool forceRefresh,
        string? profileLookupName = null)
    {
        var restore = _navigationRestores.Context;
        if (restore is not null && !_navigationRestores.IsCurrent(restore)) return;
        var tag = _currentTopLevelTag;
        var detailVersion = _artistDetailLoadVersion;
        var unifiedName = _artistInfoUnifiedArtistName;
        var cts = BeginArtistProfileLoad(out var request);
        bool CanPublish() => _artistProfileLoads.IsCurrent(request) &&
            ReferenceEquals(_artistProfileCts, cts) && ArtistInfoView.IsVisible &&
            _currentTopLevelTag == tag && detailVersion == _artistDetailLoadVersion &&
            _artistInfoUnifiedArtistName == unifiedName &&
            (restore is null || _navigationRestores.IsCurrent(restore));
        if (!CanPublish())
        {
            if (ReferenceEquals(_artistProfileCts, cts)) _artistProfileCts = null;
            cts.Dispose();
            return;
        }
        _artistInfoDisplayedRemoteRow = null;
        _nowPlayingRemoteArtistInfo = false;
        _artistInfoDisplayedId = artistId;
        if (string.IsNullOrWhiteSpace(unifiedName)) ResetArtistInfoAlbums();
        EditArtistNameButton.IsVisible = true;
        SetArtistImageActionsVisible(true);
        RefreshArtistInfoButton.IsEnabled = false;
        ArtistInfoImage.Source = null;
        ArtistInfoImagePlaceholder.IsVisible = true;
        ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoLoading;
        ArtistInfoStatusTextBlock.IsVisible = true;
        ArtistInfoBiographyTextBlock.Text = string.Empty;
        ArtistInfoSourceButton.IsVisible = false;
        ArtistInfoImageStatusText.Text = string.Empty;
        ArtistInfoImageStatusText.IsVisible = false;

        try
        {
            var artist = await Task.Run(() =>
            {
                using var db = AudioDatabase.OpenDefault();
                return db.GetArtistById(artistId);
            }, request.Token);
            if (!CanPublish()) return;
            if (artist is null)
            {
                ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoNotFound;
                return;
            }

            ArtistInfoTitleButton.Content = artist.Artist;
            SetArtistInfoFavoriteState(artist.IsFavorite);
            var language = GetProfileLanguageCode();
            var fetchedAt = artist.ProfileFetchedAt is long timestamp
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
                : (DateTimeOffset?)null;
            var needsDownload = forceRefresh ||
                string.IsNullOrWhiteSpace(artist.Biography) ||
                !string.Equals(artist.ProfileLanguage, language, StringComparison.OrdinalIgnoreCase) ||
                fetchedAt is null ||
                fetchedAt < DateTimeOffset.UtcNow.AddDays(-90);

            if (needsDownload)
            {
                ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoDownloading;
                var profile = await ArtistProfileService.DownloadAsync(
                    artist.Id,
                    profileLookupName ?? artist.Artist,
                    language,
                    downloadImage: !artist.ImageIsManual,
                    cancellationToken: cts.Token,
                    musicBrainzArtistId: artist.MusicBrainzArtistId);
                if (!CanPublish()) return;
                artist = await Task.Run(() =>
                {
                    using var db = AudioDatabase.OpenDefault();
                    db.UpdateArtistProfile(
                        artist.Id,
                        profile?.Biography,
                        profile?.ImagePath,
                        profile?.SourceUrl,
                        language);
                    return db.GetArtistById(artist.Id);
                }, request.Token);
                if (!CanPublish()) return;

                byte[]? imageData = null;
                string? imageMimeType = null;
                if (!string.IsNullOrWhiteSpace(profile?.ImagePath) && File.Exists(profile.ImagePath))
                {
                    imageData = await File.ReadAllBytesAsync(profile.ImagePath, request.Token);
                    if (!CanPublish()) return;
                    imageMimeType = GuessImageMimeType(profile.ImagePath);
                }
                await SynchronizeUnifiedArtistProfileAsync(
                    artist?.Artist,
                    profile?.Biography,
                    profile?.SourceUrl,
                    language,
                    imageData,
                    imageMimeType,
                    request.Token,
                    CanPublish);
                if (!CanPublish()) return;
            }

            if (artist is null)
                return;

            ArtistInfoBiographyTextBlock.Text = artist.Biography ?? string.Empty;
            var image = await Task.Run(
                () => CreateArtworkImage(artist.ImagePath, 1000, ignoreCache: true), request.Token);
            if (!CanPublish()) { (image as IDisposable)?.Dispose(); return; }
            ArtistInfoImage.Source = image;
            if (ArtistInfoImage.Source is null)
            {
                ArtistInfoImagePlaceholder.IsVisible = true;
                string imageMsg;
                if (string.IsNullOrWhiteSpace(artist.ImagePath))
                {
                    imageMsg = LocalizationManager.Current.ArtistInfoNoImage;
                    var diag = ArtistProfileService.LastImageDiagnostic;
                    if (!string.IsNullOrWhiteSpace(diag))
                        imageMsg += $"\n{diag}";
                }
                else if (!File.Exists(artist.ImagePath))
                    imageMsg = $"{LocalizationManager.Current.ArtistInfoImageMissing}:\n{artist.ImagePath}";
                else
                    imageMsg = $"{LocalizationManager.Current.ArtistInfoImageLoadError}:\n{artist.ImagePath}";
                ArtistInfoImageStatusText.Text = imageMsg;
                ArtistInfoImageStatusText.IsVisible = true;
            }
            else
            {
                ArtistInfoImagePlaceholder.IsVisible = false;
                ArtistInfoImageStatusText.IsVisible = false;
            }
            _artistInfoSourceUrl = artist.SourceUrl;
            ArtistInfoSourceButton.Content = _artistInfoSourceUrl?.Contains("last.fm") == true
                ? LocalizationManager.Current.ArtistInfoSourceLastFm
                : LocalizationManager.Current.ArtistInfoSource;
            ArtistInfoSourceButton.IsVisible = !(string.IsNullOrWhiteSpace(_artistInfoSourceUrl));
            ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoNotFound;
            ArtistInfoStatusTextBlock.IsVisible = string.IsNullOrWhiteSpace(artist.Biography);
            await RefreshVisibleArtistRowAsync(artist, CanPublish);
            if (!CanPublish()) return;
            if (string.IsNullOrWhiteSpace(unifiedName))
                await LoadArtistInfoAlbumsAsync(_localCatalogProvider, artistId, null, request.Token, CanPublish);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (CanPublish()) ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoDownloadFailed;
        }
        finally
        {
            if (_artistProfileCts == cts)
            {
                var canFinish = CanPublish();
                _artistProfileCts = null;
                if (canFinish) RefreshArtistInfoButton.IsEnabled = true;
            }
            cts.Dispose();
        }
    }

    /// <summary>Loads a remote artist profile while retaining view, source and request ownership.</summary>
    /// <param name="row">The source-owned artist row.</param>
    /// <param name="forceRefresh">Whether to download a fresh profile.</param>
    /// <param name="profileLookupName">Optional external lookup name.</param>
    /// <returns>A task completing the admitted profile load.</returns>
    private async Task ShowOrynivoArtistInfoAsync(
        ContentRow row,
        bool forceRefresh,
        string? profileLookupName = null)
    {
        var restore = _navigationRestores.Context;
        if (restore is not null && !_navigationRestores.IsCurrent(restore)) return;
        if (ResolveRowOrynivoServer(row) is not { } server ||
            row.Id is not long artistId ||
            row.EntityType != "OrynivoArtist")
        {
            return;
        }

        var tag = _currentTopLevelTag;
        var detailVersion = _artistDetailLoadVersion;
        var unifiedName = _artistInfoUnifiedArtistName;
        var sourceIsCurrent = CreateArtistProfileSourceGuard(server);
        if (!sourceIsCurrent()) return;
        var cts = BeginArtistProfileLoad(out var request);
        bool CanPublish() =>
            _artistProfileLoads.IsCurrent(request) &&
            ReferenceEquals(_artistProfileCts, cts) &&
            ArtistInfoView.IsVisible && _currentTopLevelTag == tag &&
            _artistDetailLoadVersion == detailVersion &&
            _artistInfoUnifiedArtistName == unifiedName && sourceIsCurrent() &&
            row.Id is long currentArtistId && currentArtistId == artistId &&
            ReferenceEquals(row.OrynivoServer ?? _activeOrynivoServer, server) &&
            (restore is null || _navigationRestores.IsCurrent(restore));
        if (!CanPublish())
        {
            if (ReferenceEquals(_artistProfileCts, cts)) _artistProfileCts = null;
            cts.Dispose();
            return;
        }

        _artistInfoDisplayedRemoteRow = row;
        _nowPlayingRemoteArtistInfo = false;
        _artistInfoDisplayedId = null;
        if (string.IsNullOrWhiteSpace(unifiedName))
            ResetArtistInfoAlbums();
        EditArtistNameButton.IsVisible = true;
        SetArtistImageActionsVisible(true);
        RefreshArtistInfoButton.IsEnabled = false;
        ArtistInfoImage.Source = null;
        ArtistInfoImagePlaceholder.IsVisible = true;
        ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoLoading;
        ArtistInfoStatusTextBlock.IsVisible = true;
        ArtistInfoBiographyTextBlock.Text = string.Empty;
        ArtistInfoSourceButton.IsVisible = false;
        ArtistInfoImageStatusText.Text = string.Empty;
        ArtistInfoImageStatusText.IsVisible = false;

        try
        {
            ArtistInfoTitleButton.Content = row.Title ?? LocalizationManager.Current.Unknown;
            SetArtistInfoFavoriteState(row.IsFavorite);
            var language = GetProfileLanguageCode();
            if (!forceRefresh &&
                (string.IsNullOrWhiteSpace(row.Biography) ||
                 string.IsNullOrWhiteSpace(row.ArtworkPath) ||
                 row.ProfileFetchedAt is null))
            {
                var cached = await _orynivoClient.GetArtistAsync(server, artistId, cts.Token);
                if (!CanPublish()) return;
                if (cached is not null)
                {
                    ApplyOrynivoArtistProfile(server, row, cached);
                    ArtistInfoTitleButton.Content = row.Title ?? LocalizationManager.Current.Unknown;
                }
            }

            var fetchedAt = row.ProfileFetchedAt is long timestamp
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
                : (DateTimeOffset?)null;
            var needsDownload = forceRefresh ||
                string.IsNullOrWhiteSpace(row.Biography) ||
                !string.Equals(row.ProfileLanguage, language, StringComparison.OrdinalIgnoreCase) ||
                fetchedAt is null ||
                fetchedAt < DateTimeOffset.UtcNow.AddDays(-90);

            if (needsDownload)
            {
                ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoDownloading;
                var profile = await ArtistProfileService.DownloadAsync(
                    artistId,
                    profileLookupName ?? row.Title ?? string.Empty,
                    language,
                    downloadImage: !row.ImageIsManual,
                    cancellationToken: cts.Token);
                if (!CanPublish()) return;

                byte[]? imageData = null;
                string? imageMimeType = null;
                if (!string.IsNullOrWhiteSpace(profile?.ImagePath) && File.Exists(profile.ImagePath))
                {
                    imageData = await File.ReadAllBytesAsync(profile.ImagePath, cts.Token);
                    if (!CanPublish()) return;
                    imageMimeType = GuessImageMimeType(profile.ImagePath);
                }

                var refreshed = await _orynivoClient.UpdateArtistProfileAsync(
                    server,
                    artistId,
                    profile?.Biography,
                    profile?.SourceUrl,
                    profile?.Language ?? language,
                    imageData,
                    imageMimeType,
                    cts.Token);
                if (!CanPublish()) return;
                if (refreshed is null)
                {
                    ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.OrynivoConnectionFailed;
                    return;
                }

                row.Biography = refreshed.Biography;
                row.SourceUrl = refreshed.SourceUrl;
                row.ProfileLanguage = refreshed.ProfileLanguage;
                row.ProfileFetchedAt = refreshed.ProfileFetchedAt;
                row.ImageIsManual = refreshed.ImageIsManual;
                if (imageData is not null)
                {
                    EnsureOrynivoArtistArtworkPaths(server, artistId, row);
                    ApplyRemoteArtwork(row, imageData);
                }
                else
                {
                    InvalidateRemoteArtworkCache(row.ArtworkPath);
                }
                DeleteOrynivoArtistListCache(server);
                await SynchronizeUnifiedArtistProfileAsync(
                    row.Title,
                    refreshed.Biography,
                    refreshed.SourceUrl,
                    refreshed.ProfileLanguage ?? language,
                    imageData,
                    imageMimeType,
                    cts.Token,
                    CanPublish);
                if (!CanPublish()) return;
            }

            ArtistInfoBiographyTextBlock.Text = row.Biography ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(row.ArtworkPath))
            {
                var image = await LoadRemoteArtworkImageAsync(row.ArtworkPath, 1000, request.Token);
                if (!CanPublish()) { (image as IDisposable)?.Dispose(); return; }
                ArtistInfoImage.Source = image;
            }

            if (ArtistInfoImage.Source is null)
            {
                ArtistInfoImagePlaceholder.IsVisible = true;
                ArtistInfoImageStatusText.Text = LocalizationManager.Current.ArtistInfoNoImage;
                ArtistInfoImageStatusText.IsVisible = true;
            }
            else
            {
                ArtistInfoImagePlaceholder.IsVisible = false;
                ArtistInfoImageStatusText.IsVisible = false;
            }

            _artistInfoSourceUrl = row.SourceUrl;
            ArtistInfoSourceButton.Content = _artistInfoSourceUrl?.Contains("last.fm") == true
                ? LocalizationManager.Current.ArtistInfoSourceLastFm
                : LocalizationManager.Current.ArtistInfoSource;
            ArtistInfoSourceButton.IsVisible = !string.IsNullOrWhiteSpace(_artistInfoSourceUrl);
            ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoNotFound;
            ArtistInfoStatusTextBlock.IsVisible = string.IsNullOrWhiteSpace(row.Biography);
            if (string.IsNullOrWhiteSpace(unifiedName))
            {
                await LoadArtistInfoAlbumsAsync(
                    CreateOrynivoCatalogProvider(server),
                    artistId,
                    server,
                    request.Token,
                    CanPublish);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (CanPublish())
                ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoDownloadFailed;
        }
        finally
        {
            if (_artistProfileCts == cts)
            {
                var canFinish = CanPublish();
                _artistProfileCts = null;
                if (canFinish) RefreshArtistInfoButton.IsEnabled = true;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Shows the artist-info view for the artist of the currently playing remote Orynivo Server
    /// track, resolving the profile through the active now-playing metadata provider so the
    /// biography and image are cached on that server.
    /// </summary>
    /// <param name="forceRefresh">Whether to force a fresh download of the artist profile.</param>
    /// <param name="profileLookupName">Optional external lookup name that leaves the library name unchanged.</param>
    /// <returns>A task completing the admitted now-playing profile load.</returns>
    private async Task ShowNowPlayingRemoteArtistInfoAsync(
        bool forceRefresh,
        string? profileLookupName = null)
    {
        if (_currentOrynivoTrackRow is not { ArtistId: long artistId } row ||
            _currentNowPlayingProvider is not OrynivoServerNowPlayingMetadataProvider provider)
        {
            return;
        }

        var restore = _navigationRestores.Context;
        if (restore is not null && !_navigationRestores.IsCurrent(restore)) return;
        var tag = _currentTopLevelTag;
        var detailVersion = _artistDetailLoadVersion;
        var unifiedName = _artistInfoUnifiedArtistName;
        var sourceIsCurrent = CreateArtistProfileSourceGuard(row.OrynivoServer);
        var playbackServer = row.OrynivoServer;
        if (!sourceIsCurrent()) return;
        var cts = BeginArtistProfileLoad(out var request);
        bool CanPublish() =>
            _artistProfileLoads.IsCurrent(request) &&
            ReferenceEquals(_artistProfileCts, cts) &&
            ArtistInfoView.IsVisible && _currentTopLevelTag == tag &&
            _artistDetailLoadVersion == detailVersion &&
            _artistInfoUnifiedArtistName == unifiedName && sourceIsCurrent() &&
            ReferenceEquals(_currentOrynivoTrackRow, row) &&
            ReferenceEquals(_currentNowPlayingProvider, provider) &&
            row.ArtistId == artistId && ReferenceEquals(row.OrynivoServer, playbackServer) &&
            (restore is null || _navigationRestores.IsCurrent(restore));
        if (!CanPublish())
        {
            if (ReferenceEquals(_artistProfileCts, cts)) _artistProfileCts = null;
            cts.Dispose();
            return;
        }

        _nowPlayingRemoteArtistInfo = true;
        _artistInfoDisplayedRemoteRow = null;
        _artistInfoDisplayedId = null;
        ResetArtistInfoAlbums();
        EditArtistNameButton.IsVisible = false;
        SetArtistImageActionsVisible(row.OrynivoServer is not null);
        RefreshArtistInfoButton.IsEnabled = false;
        ArtistInfoImage.Source = null;
        ArtistInfoImagePlaceholder.IsVisible = true;
        ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoLoading;
        ArtistInfoStatusTextBlock.IsVisible = true;
        ArtistInfoBiographyTextBlock.Text = string.Empty;
        ArtistInfoSourceButton.IsVisible = false;
        ArtistInfoImageStatusText.Text = string.Empty;
        ArtistInfoImageStatusText.IsVisible = false;

        var artistName = string.IsNullOrWhiteSpace(row.Artist)
            ? LocalizationManager.Current.Unknown
            : row.Artist;
        ArtistInfoTitleButton.Content = artistName;
        SetArtistInfoFavoriteState(row.IsFavorite);

        try
        {
            var language = GetProfileLanguageCode();
            ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoDownloading;
            var profile = await provider.GetArtistProfileForViewAsync(
                new NowPlayingArtistContext(artistId, profileLookupName ?? artistName),
                language,
                forceRefresh,
                cts.Token,
                CanPublish);
            if (!CanPublish()) return;

            byte[]? imageData = null;
            string? imageMimeType = null;
            if (!string.IsNullOrWhiteSpace(profile?.ImagePath) && File.Exists(profile.ImagePath))
            {
                imageData = await File.ReadAllBytesAsync(profile.ImagePath, cts.Token);
                if (!CanPublish()) return;
                imageMimeType = GuessImageMimeType(profile.ImagePath);
            }
            if (profile is not null)
            {
                await SynchronizeUnifiedArtistProfileAsync(
                    artistName,
                    profile.Biography,
                    profile.SourceUrl,
                    language,
                    imageData,
                    imageMimeType,
                    cts.Token,
                    CanPublish);
                if (!CanPublish()) return;
            }

            ArtistInfoBiographyTextBlock.Text = profile?.Biography ?? string.Empty;
            var image = await Task.Run(
                () => CreateArtworkImage(profile?.ImagePath, 1000, ignoreCache: true),
                request.Token);
            if (!CanPublish()) { (image as IDisposable)?.Dispose(); return; }
            ArtistInfoImage.Source = image;
            if (ArtistInfoImage.Source is null)
            {
                ArtistInfoImagePlaceholder.IsVisible = true;
                ArtistInfoImageStatusText.Text = LocalizationManager.Current.ArtistInfoNoImage;
                ArtistInfoImageStatusText.IsVisible = true;
            }
            else
            {
                ArtistInfoImagePlaceholder.IsVisible = false;
                ArtistInfoImageStatusText.IsVisible = false;
            }

            _artistInfoSourceUrl = profile?.SourceUrl;
            ArtistInfoSourceButton.Content = _artistInfoSourceUrl?.Contains("last.fm") == true
                ? LocalizationManager.Current.ArtistInfoSourceLastFm
                : LocalizationManager.Current.ArtistInfoSource;
            ArtistInfoSourceButton.IsVisible = !string.IsNullOrWhiteSpace(_artistInfoSourceUrl);
            ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoNotFound;
            ArtistInfoStatusTextBlock.IsVisible = string.IsNullOrWhiteSpace(profile?.Biography);
            if (row.OrynivoServer is { } npServer)
                await LoadArtistInfoAlbumsAsync(
                    CreateOrynivoCatalogProvider(npServer),
                    artistId,
                    npServer,
                    request.Token,
                    CanPublish);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (CanPublish())
                ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistInfoDownloadFailed;
        }
        finally
        {
            if (_artistProfileCts == cts)
            {
                var canFinish = CanPublish();
                _artistProfileCts = null;
                if (canFinish) RefreshArtistInfoButton.IsEnabled = true;
            }
            cts.Dispose();
        }
    }

    /// <summary>Captures transient server identity for profile publication without persisting connection data.</summary>
    /// <param name="server">The owning server, or null for an unavailable source.</param>
    /// <returns>A predicate requiring the original and configured source to retain its identity.</returns>
    private Func<bool> CreateArtistProfileSourceGuard(OrynivoServerSettings? server)
    {
        if (server is null) return () => false;
        var id = server.Id;
        var name = server.Name;
        var url = server.BaseUrl;
        var key = server.ApiKey;
        var profile = server.ProfileId;
        bool Matches(OrynivoServerSettings candidate) =>
            candidate.Id == id && candidate.Name == name && candidate.BaseUrl == url &&
            candidate.ApiKey == key && candidate.ProfileId == profile;
        return () => Matches(server) && (_settings.OrynivoServers ?? []).Any(Matches);
    }

    /// <summary>Refreshes a bound artist row while retaining its background request and row identity.</summary>
    /// <param name="row">The bound local artist row.</param>
    /// <returns>A task completing the automatic profile refresh or its abandonment.</returns>
    private async Task EnsureArtistProfileAsync(ContentRow row)
    {
        if (row.Id is not long artistId || row.EntityType != "Artist")
            return;

        var language = GetProfileLanguageCode();
        var fetchedAt = row.ProfileFetchedAt is long timestamp
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
            : (DateTimeOffset?)null;
        if (fetchedAt >= DateTimeOffset.UtcNow.AddDays(-90) &&
            string.Equals(row.ProfileLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var background = _backgroundArtistLoadCts;
        var backgroundToken = background.Token;
        var tag = _currentTopLevelTag;
        var artistName = row.Title;
        var imageIsManual = row.ImageIsManual;
        var tableSource = ContentDataGrid.IsVisible ? ContentDataGrid.ItemsSource : null;
        var artworkSource = ArtistArtworkListBox.IsVisible ? ArtistArtworkListBox.ItemsSource : null;
        bool CanPublish() => ReferenceEquals(_backgroundArtistLoadCts, background) &&
            !backgroundToken.IsCancellationRequested && _currentTopLevelTag == tag &&
            GetProfileLanguageCode() == language && row.Id is long currentId && currentId == artistId &&
            row.Title == artistName && row.ImageIsManual == imageIsManual &&
            ((tableSource is not null && ContentDataGrid.IsVisible && ReferenceEquals(ContentDataGrid.ItemsSource, tableSource)) ||
             (artworkSource is not null && ArtistArtworkListBox.IsVisible && ReferenceEquals(ArtistArtworkListBox.ItemsSource, artworkSource)));
        if (!CanPublish()) return;
        if (_artistProfilesLoading.TryGetValue(artistId, out var active) && ReferenceEquals(active, background))
            return;
        _artistProfilesLoading[artistId] = background;

        // Mark this row as fetched for the current session up front. If the download or
        // database write below fails (e.g. no network, or a concurrent library scan holds
        // the SQLite write lock), the freshness check above must still short-circuit so the
        // same row does not re-trigger a network request and database open on every scroll
        // pass. A freshly merged library has thousands of artists without cached profiles;
        // retrying each visible row repeatedly previously flooded the UI thread and froze
        // the artist table.
        row.ProfileLanguage = language;
        row.ProfileFetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            var profile = await ArtistProfileService.DownloadAsync(
                artistId,
                row.Title ?? string.Empty,
                language,
                downloadImage: !row.ImageIsManual,
                cancellationToken: backgroundToken);
            if (!CanPublish()) return;

            // Persist the (possibly negative) result and decode the artwork off the UI thread.
            // Opening the database runs connection pragmas and schema DDL, which must never
            // happen on the UI thread once per visible artist row.
            var imagePath = profile?.ImagePath;
            var (artwork, thumbnail) = await Task.Run(() =>
            {
                using var db = AudioDatabase.OpenDefault();
                backgroundToken.ThrowIfCancellationRequested();
                db.UpdateArtistProfile(
                    artistId,
                    profile?.Biography,
                    profile?.ImagePath,
                    profile?.SourceUrl,
                    language);
                return string.IsNullOrWhiteSpace(imagePath)
                    ? ((IImage?)null, (IImage?)null)
                    : (CreateArtworkImage(imagePath, 320, ignoreCache: true),
                       CreateArtworkImage(imagePath, 96, ignoreCache: true));
            }, backgroundToken);

            if (!CanPublish())
            {
                (artwork as IDisposable)?.Dispose();
                (thumbnail as IDisposable)?.Dispose();
                return;
            }

            // The awaits above do not suppress the captured context, so these row updates
            // run on the UI thread as required by INotifyPropertyChanged.
            row.Biography = profile?.Biography;
            row.SourceUrl = profile?.SourceUrl;
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                row.ArtworkPath = imagePath;
                row.ThumbnailPath = imagePath;
                row.ArtworkLoadCompleted = false;
                row.Artwork = artwork;
                row.Thumbnail = thumbnail;
                row.ArtworkLoadCompleted = true;
            }
        }
        catch
        {
            // Künstlerkarten bleiben auch ohne Netzwerk nutzbar.
        }
        finally
        {
            if (_artistProfilesLoading.TryGetValue(artistId, out var finishingOwner) && ReferenceEquals(finishingOwner, background))
                _artistProfilesLoading.Remove(artistId);
        }
    }

    /// <summary>Refreshes a retained artist row only after both image decodes remain admitted.</summary>
    /// <param name="artist">Resolved local profile metadata.</param>
    /// <param name="canPublish">Optional owning profile guard.</param>
    /// <returns>A task completing after row publication or abandonment.</returns>
    private async Task RefreshVisibleArtistRowAsync(ArtistInfo artist, Func<bool>? canPublish = null)
    {
        if (canPublish is not null && !canPublish()) return;
        var rows = ContentDataGrid.ItemsSource as IEnumerable<ContentRow>
            ?? ArtistArtworkListBox.ItemsSource as IEnumerable<ContentRow>;
        var comparisonKey = ArtistNameNormalizer.CreateComparisonKey(artist.Artist);
        var row = rows?.FirstOrDefault(item =>
            (item.EntityType == "Artist" && item.Id == artist.Id) ||
            (item.EntityType == "UnifiedArtist" &&
             ArtistNameNormalizer.CreateComparisonKey(item.Title) == comparisonKey));
        if (row is null)
            return;

        var artwork = await Task.Run(() => CreateArtworkImage(artist.ImagePath, 320, ignoreCache: true));
        if (canPublish is not null && !canPublish()) { (artwork as IDisposable)?.Dispose(); return; }
        var thumbnail = await Task.Run(() => CreateArtworkImage(artist.ImagePath, 96, ignoreCache: true));
        if (canPublish is not null && !canPublish())
        {
            (artwork as IDisposable)?.Dispose();
            (thumbnail as IDisposable)?.Dispose();
            return;
        }
        row.Biography = artist.Biography;
        row.SourceUrl = artist.SourceUrl;
        row.ProfileLanguage = artist.ProfileLanguage;
        row.ProfileFetchedAt = artist.ProfileFetchedAt;
        row.ImageIsManual = artist.ImageIsManual;
        row.ArtworkPath = artist.ImagePath;
        row.ThumbnailPath = artist.ImagePath;
        row.ArtworkLoadCompleted = false;
        row.Artwork = artwork;
        row.Thumbnail = thumbnail;
        row.ArtworkLoadCompleted = true;
    }

    private string GetProfileLanguageCode() => _settings.Language switch
    {
        Orynivo.Localization.Language.German => "de",
        Orynivo.Localization.Language.French => "fr",
        Orynivo.Localization.Language.Spanish => "es",
        Orynivo.Localization.Language.Russian => "ru",
        Orynivo.Localization.Language.ChineseSimplified => "zh",
        Orynivo.Localization.Language.Hindi => "hi",
        _ => "en"
    };

    /// <summary>Installs local profile and background ownership before cancelling older work.</summary>
    /// <param name="request">Publication identity of the newly installed profile.</param>
    /// <returns>The cancellation source owned and disposed by the profile caller.</returns>
    private CancellationTokenSource BeginArtistProfileLoad(out NavigationLoadOwnership.Request request)
    {
        var previousProfile = _artistProfileCts;
        var previousBackground = _backgroundArtistLoadCts;
        var cancellation = new CancellationTokenSource();
        _artistProfileCts = cancellation;
        _backgroundArtistLoadCts = new CancellationTokenSource();
        request = _artistProfileLoads.Begin(cancellation.Token);
        previousProfile?.Cancel();
        previousBackground.Cancel();
        return cancellation;
    }

    /// <summary>Detaches profile and replaces background ownership before cancellation callbacks.</summary>
    private void CancelArtistProfileLoad()
    {
        var profile = _artistProfileCts;
        var background = _backgroundArtistLoadCts;
        _artistProfileCts = null;
        _artistProfileLoads.Invalidate();
        _backgroundArtistLoadCts = new CancellationTokenSource();
        profile?.Cancel();
        background.Cancel();
    }
}
