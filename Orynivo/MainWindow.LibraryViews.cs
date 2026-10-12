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
/// Unified local/remote library view loading, row merging, and track, album,
/// and artist row conversion for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Maps a remote track and optionally registers its admitted playback metadata.</summary>
    /// <param name="server">Owning server connection.</param>
    /// <param name="track">Remote track metadata.</param>
    /// <param name="registerRemoteMetadata">Whether the caller owns playback publication.</param>
    /// <returns>The source-aware playable row.</returns>
    private ContentRow ToOrynivoTrackContentRow(OrynivoServerSettings server, OrynivoTrackInfo track,
        bool registerRemoteMetadata = true)
    {
        var streamUrl = OrynivoServerClient.GetStreamUrl(server, track.Id);
        var row = new ContentRow
        {
            Title = track.Title?.Trim() ?? track.FileName.Trim(),
            AlphabetIndexText = track.SortTitle?.Trim() ?? track.Title?.Trim() ?? track.FileName.Trim(),
            Id = track.Id,
            Artist = track.Artist,
            Album = track.Album,
            AlbumArtist = track.AlbumArtist,
            Year = track.Year?.ToString(CultureInfo.CurrentCulture),
            TrackNumber = FormatPartNumber(track.TrackNumber, track.TrackTotal),
            DiscNumber = FormatPartNumber(track.DiscNumber, track.DiscTotal),
            Duration = FormatSeconds(track.Duration),
            Genre = track.Genre,
            Format = track.Format?.ToUpperInvariant(),
            Bitrate = track.Bitrate is > 0 ? $"{track.Bitrate:N0} kbps" : null,
            SampleRate = track.SampleRate is > 0 ? $"{track.SampleRate:N0} Hz" : null,
            SampleRateHz = track.SampleRate,
            BitDepth = track.BitDepth is > 0 ? $"{track.BitDepth:N0} Bit" : null,
            Channels = track.Channels?.ToString(CultureInfo.CurrentCulture),
            ChannelCount = track.Channels,
            Composer = track.Composer,
            Bpm = track.Bpm?.ToString(CultureInfo.CurrentCulture),
            FileName = track.FileName,
            FileSize = FormatFileSize(track.FileSize),
            AddedAt = track.AddedAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(track.AddedAt.Value)
                    .ToLocalTime()
                    .ToString("d", CultureInfo.CurrentCulture)
                : null,
            ReplayGainTrack = FormatReplayGainDisplay(track.ReplayGainTrack),
            ReplayGainAlbum = FormatReplayGainDisplay(track.ReplayGainAlbum),
            UserRating = track.UserRating,
            MusicBrainzRating = track.MusicBrainzRating,
            MusicBrainzRatingVotes = track.MusicBrainzRatingVotes,
            MusicBrainzTrackId = track.MusicBrainzTrackId,
            MusicBrainzRatingFetchedAt = track.MusicBrainzRatingFetchedAt,
            FilePath = streamUrl,
            SourcePath = track.SourcePath ?? track.Path,
            ArtworkPath = OrynivoServerClient.GetTrackArtworkUrl(server, track.Id, 320),
            ThumbnailPath = OrynivoServerClient.GetTrackArtworkUrl(server, track.Id, 96),
            IsFavorite = IsOrynivoFavorite(server, "Track", track.Id),
            ArtistId = track.ArtistId,
            AlbumId = track.AlbumId,
            EntityType = "OrynivoTrack",
            ExternalId = track.Id.ToString(CultureInfo.InvariantCulture),
            KnownDuration = track.Duration.HasValue ? TimeSpan.FromSeconds(track.Duration.Value) : null,
            OrynivoServer = server
        };
        if (registerRemoteMetadata) _orynivoTracksByUrl[streamUrl] = row;
        return row;
    }

    // ------------------------------------------------------------------
    // DB-Abfragen
    // ------------------------------------------------------------------

    /// <summary>Queries local rows, optionally propagating database failures to the unified loader.</summary>
    /// <param name="view">Requested local view.</param>
    /// <param name="searchQuery">Optional free-text search.</param>
    /// <param name="requireComplete">Whether database failures must propagate rather than resemble an empty catalog.</param>
    /// <param name="context">Optional captured shared-view inputs; other callers retain current synchronous semantics.</param>
    /// <returns>Resolved local rows.</returns>
    private List<ContentRow> QueryRows(string view, string? searchQuery = null,
        bool requireComplete = false, LibraryCatalogLoadContext? context = null)
    {
        try
        {
            using var db = AudioDatabase.OpenDefault();

            return view switch
            {
                "Search" => db.GetTrackListByIds(TrackSearchIndex.SearchByCategory(searchQuery ?? string.Empty).Tracks.Ids)
                    .Select(ToTrackContentRow)
                    .ToList(),

                "Artists" => db.GetArtistsLite()
                    .Select(LocalLibraryCatalogProvider.ToCatalogArtist)
                    .Where(a => !(context?.ArtistFavoritesOnly ?? _artistFavoritesOnly) || a.IsFavorite)
                    .Select(artist => ToCatalogArtistContentRow(artist))
                    .ToList(),

                "Albums" => ((context is null ? _activeArtistFilterId : context.ArtistId) is long artistId
                        ? db.GetAlbumsByArtist(artistId, (context?.AlbumArtwork ?? _showAlbumArtworkView))
                        : db.GetAlbumsLite((context?.AlbumArtwork ?? _showAlbumArtworkView)))
                    .Select(LocalLibraryCatalogProvider.ToCatalogAlbum)
                    .Where(a => !(context?.AlbumFavoritesOnly ?? _albumFavoritesOnly) || a.IsFavorite)
                    .Select(album => ToCatalogAlbumContentRow(album))
                    .ToList(),

                _ => ((context is null ? _activeAlbumFilterId : context.AlbumId) is long albumId
                        ? db.GetTrackListByAlbum(albumId)
                        : db.GetTrackList()) // "Tracks" and fallback
                    .Select(LocalLibraryCatalogProvider.ToCatalogTrack)
                    .Select(track => ToCatalogTrackContentRow(track))
                    .ToList()
            };
        }
        catch when (!requireComplete) { return []; }
    }

    /// <summary>Captures live filter selections for existing local-only callers.</summary>
    /// <returns>Local rows matching the copied selections.</returns>
    private List<ContentRow> GetFilteredTrackRows() => GetFilteredTrackRows(
        new TrackFacetFilterSnapshot(_trackFavoritesOnly, _selectedTrackGenres,
            _selectedTrackFormats, _selectedTrackBitrates, _selectedTrackSources));

    /// <summary>Queries local tracks using operation-owned selections.</summary>
    /// <param name="filters">Copied selections captured before background work.</param>
    /// <returns>Matching local track rows.</returns>
    private List<ContentRow> GetFilteredTrackRows(TrackFacetFilterSnapshot filters)
    {
        using var db = AudioDatabase.OpenDefault();
        if (!filters.HasFilters)
        {
            return db.GetTrackList()
                .Select(ToTrackContentRow)
                .ToList();
        }

        var facets = db.GetTrackFacets();
        var ids = facets
            .Where(filters.Matches)
            .Select(f => f.Id)
            .ToList();
        return db.GetTrackListFiltered(ids)
            .Select(ToTrackContentRow)
            .ToList();
    }

    private sealed class StringNotEmptyConverter : IValueConverter
    {
        public static readonly StringNotEmptyConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string text && !string.IsNullOrWhiteSpace(text);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Loads independent sources and publishes only the current combined view, caching complete results.</summary>
    /// <param name="tag">Shared Artists, Albums, or Tracks view.</param>
    /// <param name="preservePosition">Whether to retain the selection and scroll position at publication.</param>
    /// <returns>A task completing after publication or rejection of the superseded load.</returns>
    private async Task BindLocalRowsAndStartRemoteAppendAsync(string tag, bool preservePosition = false)
    {
        var diagnosticStopwatch = Stopwatch.StartNew();
        LogUiDiagnostics($"BindLocalRowsAndStartRemoteAppendAsync start tag={tag}");
        var previousCancellation = _unifiedLibraryAppendCts;
        _unifiedLibraryAppendCts = new CancellationTokenSource();
        var cancellationToken = _unifiedLibraryAppendCts.Token;
        var request = _unifiedLibraryLoads.Begin(CreateUnifiedLibraryViewCacheKey(tag), cancellationToken);
        CancelAndDispose(ref previousCancellation);
        if (!_unifiedLibraryLoads.IsCurrent(request)) return;
        UpdateUnifiedLibraryLoadNotice();
        if (_unifiedLibraryLoads.TryGet(request, CanCacheUnifiedLibraryView(tag), out var cachedResult))
        {
            var cachedRows = cachedResult!.Rows;
            _unifiedLibraryLoadResult = cachedResult;
            _unifiedLibraryPublishedRequest = request;
            UpdateUnifiedLibraryLoadNotice();
            ApplyColumns(tag);
            ContentDataGrid.ItemsSource = cachedRows;
            BindUnifiedArtworkRowsIfVisible(tag, cachedRows);
            UpdateAlphabetIndex(cachedRows, true);
            UpdateUnifiedContentCount(tag, cachedRows.Count);
            LogUiDiagnostics(
                $"BindLocalRowsAndStartRemoteAppendAsync cache hit tag={tag} count={cachedRows.Count} elapsed={diagnosticStopwatch.ElapsedMilliseconds}ms");
            return;
        }
        var context = new LibraryCatalogLoadContext(_settings.OrynivoServers ?? [],
            ActiveUserProfile.OrynivoServerFavorites,
            new TrackFacetFilterSnapshot(_trackFavoritesOnly, _selectedTrackGenres,
                _selectedTrackFormats, _selectedTrackBitrates, _selectedTrackSources),
            _artistFavoritesOnly, _albumFavoritesOnly, _showAlbumArtworkView,
            _activeArtistFilterId, _activeAlbumFilterId);
        var result = await _unifiedLibraryLoads.LoadAsync(request,
        [
            captured => LibrarySourceLoader.LoadAsync<ContentRow>("local",
                async token => await Task.Run(() => tag == "Tracks"
                    ? GetFilteredTrackRows(context.Filters)
                    : QueryRows(tag, requireComplete: true, context: context), token), captured),
            captured => LoadRemoteUnifiedRowsAsync(tag, context, captured)
        ]);

        if (result is null || !_unifiedLibraryLoads.IsCurrent(request) ||
            !string.Equals(_currentTopLevelTag, tag, StringComparison.Ordinal))
        {
            LogUiDiagnostics($"BindLocalRowsAndStartRemoteAppendAsync canceled before combined bind tag={tag} version={request.Version}");
            return;
        }

        var sortedRows = SortUnifiedRows(result.Rows);
        if (tag == "Artists")
            sortedRows = MergeUnifiedArtistRows(sortedRows);
        else if (tag == "Albums")
            sortedRows = MergeLogicalAlbumRows(sortedRows);
        result = result with { Rows = sortedRows };
        foreach (var row in sortedRows)
            if (row.OrynivoServer is not null && row.EntityType == "OrynivoTrack" && row.FilePath is { } path)
                _orynivoTracksByUrl[path] = row;
        _unifiedLibraryLoadResult = result;
        _unifiedLibraryPublishedRequest = request;
        _unifiedLibraryLoads.TryStore(request, CreateUnifiedLibraryViewCacheKey(tag),
            CanCacheUnifiedLibraryView(tag), result);
        LogUiDiagnostics($"Unified library outcome tag={tag} status={result.Status} sources={result.Sources.Count} failed={result.Sources.Count(source => source.Status != LibrarySourceLoadStatus.Success)}");
        LogUiDiagnostics(
            $"BindLocalRowsAndStartRemoteAppendAsync combined rows sorted tag={tag} count={sortedRows.Count} elapsed={diagnosticStopwatch.ElapsedMilliseconds}ms");
        var selectedRow = preservePosition ? GetSelectedContentRow() : null;
        var verticalOffset = preservePosition ? CaptureCurrentVerticalOffset() : (double?)null;
        ApplyColumns(tag);
        ContentDataGrid.ItemsSource = sortedRows;
        BindUnifiedArtworkRowsIfVisible(tag, sortedRows);
        UpdateAlphabetIndex(sortedRows, true);
        UpdateUnifiedContentCount(tag, sortedRows.Count);
        UpdateUnifiedLibraryLoadNotice();
        if (preservePosition)
            RestoreSelectionFromCurrentItems(selectedRow?.Id, verticalOffset, selectedRow?.SourceKey,
                () => _unifiedLibraryLoads.IsCurrent(request) &&
                      string.Equals(_currentTopLevelTag, tag, StringComparison.Ordinal));
        LogUiDiagnostics(
            $"BindLocalRowsAndStartRemoteAppendAsync combined bind completed tag={tag} count={sortedRows.Count} elapsed={diagnosticStopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>Combines independent remote source outcomes in configured source order.</summary>
    /// <param name="tag">Shared library view.</param>
    /// <param name="context">Copied source, profile, favorite and filter inputs.</param>
    /// <param name="cancellationToken">Owning navigation cancellation.</param>
    /// <returns>Available remote rows with explicit per-source outcomes.</returns>
    private Task<LibraryLoadResult<ContentRow>> LoadRemoteUnifiedRowsAsync(
        string tag,
        LibraryCatalogLoadContext context,
        CancellationToken cancellationToken) =>
        LibrarySourceBatchLoader.LoadAsync<OrynivoServerSettings, ContentRow>(context.Servers,
            server => GetServerSourceKey(server.Id),
            (server, token) => LoadRemoteUnifiedServerRowsAsync(tag, server, context, token),
            cancellationToken, maximumConcurrency: Math.Max(1, context.Servers.Count),
            timeout: TimeSpan.FromSeconds(20));

    /// <summary>Maps one server's strict catalog request inside the shared scheduler's source budget.</summary>
    /// <param name="tag">Shared Artists, Albums, or Tracks view tag.</param>
    /// <param name="server">Owning Orynivo Server.</param>
    /// <param name="context">Copied personal selections and connection context.</param>
    /// <param name="cancellationToken">Scheduler-owned navigation and budget cancellation.</param>
    /// <returns>Mapped rows; failures propagate to the scheduler's sanitized outcome.</returns>
    private async Task<IReadOnlyList<ContentRow>> LoadRemoteUnifiedServerRowsAsync(
        string tag,
        OrynivoServerSettings server,
        LibraryCatalogLoadContext context,
        CancellationToken cancellationToken)
    {
        var provider = new OrynivoServerLibraryCatalogProvider(server, _orynivoClient,
            (entity, id) => context.Favorites.Contains(GetOrynivoFavoriteKey(server.Id, entity, id)), requireComplete: true);
        return tag switch
        {
            "Artists" => (await LoadAllOrynivoArtistsAsync(server, cancellationToken, context.Favorites))
                .Where(artist => !context.ArtistFavoritesOnly || artist.IsFavorite)
                .Select(artist => ToCatalogArtistContentRow(artist, server))
                .ToList(),
            "Albums" => (await LoadAllOrynivoAlbumsAsync(server, cancellationToken, context.Favorites))
                .Where(album => !context.AlbumFavoritesOnly || album.IsFavorite)
                .Select(album => ToCatalogAlbumContentRow(album, server))
                .ToList(),
            "Tracks" => (await LoadRemoteTracksForUnifiedViewAsync(server, provider, context, cancellationToken))
                .Select(track => ToCatalogTrackContentRow(track, server, registerRemoteMetadata: false))
                .ToList(),
            _ => []
        };
    }

    /// <summary>Returns cached plain or synchronized lyrics for the current track.</summary>
    /// <returns>Cached lyrics, or <see langword="null"/> when no eligible track or lyrics exist.</returns>
    private async Task<string?> GetCurrentCachedLyricsForToolAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
            return null;
        try
        {
            var provider = _currentNowPlayingProvider ?? _localNowPlayingProvider;
            var lyrics = await provider.GetCachedLyricsAsync(BuildNowPlayingTrackContext(_currentFilePath));
            return !string.IsNullOrWhiteSpace(lyrics?.Plain)
                ? lyrics.Plain
                : lyrics?.Synced;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Starts a normal library scan on the configured server with the supplied name.</summary>
    /// <param name="serverName">Exact server display name.</param>
    /// <returns><see langword="true"/> when the server exists and accepted the scan request.</returns>
    private async Task<bool> TriggerOrynivoServerScanByNameAsync(string serverName)
    {
        var server = (_settings.OrynivoServers ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, serverName?.Trim(), StringComparison.OrdinalIgnoreCase));
        return server is not null && await _orynivoClient.TriggerScanAsync(server);
    }

    /// <summary>Resolves strict remote track or facet requests for the shared Tracks view.</summary>
    /// <param name="server">Owning source.</param>
    /// <param name="provider">Strict catalog provider used for filtered ID resolution.</param>
    /// <param name="context">Copied filters and remote favorites.</param>
    /// <param name="cancellationToken">Source cancellation.</param>
    /// <returns>Available tracks after the complete request succeeds.</returns>
    private async Task<IReadOnlyList<LibraryCatalogTrack>> LoadRemoteTracksForUnifiedViewAsync(
        OrynivoServerSettings server,
        ILibraryCatalogProvider provider,
        LibraryCatalogLoadContext context,
        CancellationToken cancellationToken)
    {
        if (context.Filters.HasFilters)
        {
            var facets = await _orynivoClient.GetTrackFacetsAsync(server, cancellationToken, requireComplete: true);
            var ids = facets
                .Select(facet => facet with
                {
                    IsFavorite = context.Favorites.Contains(GetOrynivoFavoriteKey(server.Id, "Track", facet.Id)),
                    SourceKey = GetServerSourceKey(server.Id)
                })
                .Where(context.Filters.Matches)
                .Select(facet => facet.Id)
                .ToList();
            return ids.Count == 0
                ? []
                : await provider.GetTracksByIdsAsync(ids, cancellationToken);
        }

        return await LoadAllOrynivoTracksAsync(server, cancellationToken, context.Favorites);
    }

    private void BindUnifiedRows(string tag, List<ContentRow> rows)
    {
        rows = SortUnifiedRows(rows);
        if (tag == "Albums")
            rows = MergeLogicalAlbumRows(rows);
        ApplyColumns(tag);
        ContentDataGrid.ItemsSource = rows;
        BindUnifiedArtworkRowsIfVisible(tag, rows);
        UpdateAlphabetIndex(rows, true);
        UpdateUnifiedContentCount(tag, rows.Count);
    }

    private void BindUnifiedArtworkRowsIfVisible(string tag, IReadOnlyList<ContentRow> rows)
    {
        if (tag == "Albums" && _showAlbumArtworkView)
            BindArtworkRows(tag, rows);
        else if (tag == "Artists" && _showArtistArtworkView)
            BindArtworkRows(tag, rows);
    }

    private void UpdateUnifiedContentCount(string tag, int count)
    {
        ContentCountTextBlock.Text = tag == "Tracks"
            ? LocalizationManager.FormatTrackCount(count)
            : LocalizationManager.FormatEntryCount(count);
    }

    private static List<ContentRow> SortUnifiedRows(IEnumerable<ContentRow> rows) =>
        rows
            .OrderBy(row => row.AlphabetIndexText ?? row.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.SourceName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static List<ContentRow> MergeUnifiedArtistRows(IEnumerable<ContentRow> rows) =>
        rows
            .GroupBy(row => ArtistNameNormalizer.CreateComparisonKey(row.Title), StringComparer.Ordinal)
            .Select(group =>
            {
                var candidates = group.ToList();
                var row = candidates.FirstOrDefault(candidate => candidate.EntityType == "Artist")
                          ?? candidates[0];
                if (candidates.Count > 1)
                {
                    var imageSource = candidates
                        .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ArtworkPath))
                        .OrderByDescending(candidate => candidate.ImageIsManual)
                        .ThenByDescending(candidate => candidate.ProfileFetchedAt ?? 0)
                        .FirstOrDefault();
                    var profileSource = candidates
                        .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Biography))
                        .OrderByDescending(candidate => candidate.ProfileFetchedAt ?? 0)
                        .FirstOrDefault();
                    if (imageSource is not null)
                    {
                        row.ArtworkPath = imageSource.ArtworkPath;
                        row.ThumbnailPath = imageSource.ThumbnailPath;
                        row.ImageIsManual = imageSource.ImageIsManual;
                    }
                    if (profileSource is not null)
                    {
                        row.Biography = profileSource.Biography;
                        row.SourceUrl = profileSource.SourceUrl;
                        row.ProfileLanguage = profileSource.ProfileLanguage;
                        row.ProfileFetchedAt = profileSource.ProfileFetchedAt;
                    }
                    row.EntityType = "UnifiedArtist";
                    row.IsFavorite = candidates.Any(candidate => candidate.IsFavorite);
                }
                return row;
            })
            .OrderBy(row => row.AlphabetIndexText ?? row.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static List<ContentRow> MergeLogicalAlbumRows(IEnumerable<ContentRow> rows) =>
        rows
            .Where(row => IsKnownAlbumTitle(row.Title))
            .GroupBy(row =>
            {
                var title = row.Title?.Trim() ?? string.Empty;
                var artistKey = ArtistNameNormalizer.CreateComparisonKey(row.Artist);
                return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artistKey)
                    ? $"{row.SourceKey}\u001f{row.Id?.ToString(CultureInfo.InvariantCulture)}"
                    : $"{title.ToUpperInvariant()}\u001f{artistKey}";
            }, StringComparer.Ordinal)
            .Select(group =>
            {
                var candidates = group.ToList();
                var row = candidates
                    .OrderByDescending(candidate => candidate.OrynivoServer is null)
                    .ThenByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.ArtworkPath))
                    .ThenBy(candidate => candidate.Id)
                    .First();
                var artworkSource = candidates.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.ArtworkPath));
                if (artworkSource is not null)
                {
                    row.ArtworkPath = artworkSource.ArtworkPath;
                    row.ThumbnailPath = artworkSource.ThumbnailPath;
                }
                row.LogicalAlbumIds = candidates
                    .Select(candidate => candidate.AlbumId ?? candidate.Id)
                    .OfType<long>()
                    .Distinct()
                    .ToList();
                row.LogicalAlbumParts = candidates
                    .Select(candidate => new LogicalAlbumPart(
                        (candidate.AlbumId ?? candidate.Id)!.Value,
                        candidate.ArtistId,
                        candidate.OrynivoServer))
                    .DistinctBy(part => $"{part.Server?.Id ?? LocalSourceKey}:{part.AlbumId}")
                    .ToList();
                if (candidates.Select(candidate => candidate.SourceKey).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    row.EntityType = "UnifiedAlbum";
                row.IsFavorite = candidates.Any(candidate => candidate.IsFavorite);
                return row;
            })
            .OrderBy(row => row.AlphabetIndexText ?? row.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.SourceName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool IsKnownAlbumTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalized = title.Trim();
        if (string.Equals(normalized, LocalizationManager.Current.Unknown, StringComparison.CurrentCultureIgnoreCase))
            return false;

        return normalized.ToUpperInvariant() is not
            ("UNKNOWN" or "(UNKNOWN)" or
             "UNBEKANNT" or "(UNBEKANNT)" or
             "INCONNU" or "(INCONNU)" or
             "DESCONOCIDO" or "(DESCONOCIDO)");
    }

    private static ContentRow ToTrackContentRow(TrackListInfo t) => new()
    {
        Title = t.Title?.Trim() ?? t.FileName.Trim(),
        AlphabetIndexText = t.SortTitle?.Trim() ?? t.Title?.Trim() ?? t.FileName.Trim(),
        Id = t.Id,
        Artist = t.Artist,
        Album = t.Album,
        AlbumArtist = t.AlbumArtist,
        Year = t.Year?.ToString(CultureInfo.CurrentCulture),
        TrackNumber = FormatPartNumber(t.TrackNumber, t.TrackTotal),
        DiscNumber = FormatPartNumber(t.DiscNumber, t.DiscTotal),
        Duration = FormatSeconds(t.Duration),
        Genre = t.Genre,
        Format = t.Format?.ToUpperInvariant(),
        Bitrate = t.Bitrate is > 0 ? $"{t.Bitrate:N0} kbps" : null,
        SampleRate = t.SampleRate is > 0 ? $"{t.SampleRate:N0} Hz" : null,
        BitDepth = t.BitDepth is > 0 ? $"{t.BitDepth:N0} Bit" : null,
        Channels = t.Channels?.ToString(CultureInfo.CurrentCulture),
        Composer = t.Composer,
        Bpm = t.Bpm?.ToString(CultureInfo.CurrentCulture),
        CamelotKey = t.CamelotKey,
        FileName = t.FileName,
        FileSize = FormatFileSize(t.FileSize),
        AddedAt = DateTimeOffset.FromUnixTimeSeconds(t.AddedAt)
            .ToLocalTime()
            .ToString("d", CultureInfo.CurrentCulture),
        ReplayGainTrack = FormatReplayGainDisplay(t.ReplayGainTrack),
        ReplayGainAlbum = FormatReplayGainDisplay(t.ReplayGainAlbum),
        UserRating = t.UserRating,
        MusicBrainzRating = t.MusicBrainzRating,
        MusicBrainzRatingVotes = t.MusicBrainzRatingVotes,
        MusicBrainzTrackId = t.MusicBrainzTrackId,
        MusicBrainzRatingFetchedAt = t.MusicBrainzRatingFetchedAt,
        FilePath = t.Path,
        SourcePath = t.Path,
        IsFavorite = t.IsFavorite,
        ArtistId = t.ArtistId,
        AlbumId = t.AlbumId,
        KnownDuration = t.Duration.HasValue ? TimeSpan.FromSeconds(t.Duration.Value) : null,
        EntityType = "Track"
    };

    private ContentRow ToCatalogTrackContentRow(LibraryCatalogTrack track, OrynivoServerSettings? server = null, bool registerRemoteMetadata = true)
    {
        server ??= track.Source == LibraryCatalogSource.OrynivoServer ? _activeOrynivoServer : null;
        var row = new ContentRow
        {
            Title = track.Title?.Trim() ?? track.FileName.Trim(),
            AlphabetIndexText = track.SortTitle?.Trim() ?? track.Title?.Trim() ?? track.FileName.Trim(),
            Id = track.Id,
            Artist = track.Artist,
            Album = track.Album,
            AlbumArtist = track.AlbumArtist,
            Year = track.Year?.ToString(CultureInfo.CurrentCulture),
            TrackNumber = FormatPartNumber(track.TrackNumber, track.TrackTotal),
            DiscNumber = FormatPartNumber(track.DiscNumber, track.DiscTotal),
            Duration = FormatSeconds(track.Duration),
            Genre = track.Genre,
            Format = track.Format?.ToUpperInvariant(),
            Bitrate = track.Bitrate is > 0 ? $"{track.Bitrate:N0} kbps" : null,
            SampleRate = track.SampleRate is > 0 ? $"{track.SampleRate:N0} Hz" : null,
            SampleRateHz = track.SampleRate,
            BitDepth = track.BitDepth is > 0 ? $"{track.BitDepth:N0} Bit" : null,
            Channels = track.Channels?.ToString(CultureInfo.CurrentCulture),
            ChannelCount = track.Channels,
            Composer = track.Composer,
            Bpm = track.Bpm?.ToString(CultureInfo.CurrentCulture),
            CamelotKey = track.CamelotKey,
            FileName = track.FileName,
            FileSize = FormatFileSize(track.FileSize),
            AddedAt = track.AddedAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(track.AddedAt.Value)
                    .ToLocalTime()
                    .ToString("d", CultureInfo.CurrentCulture)
                : null,
            ReplayGainTrack = FormatReplayGainDisplay(track.ReplayGainTrack),
            ReplayGainAlbum = FormatReplayGainDisplay(track.ReplayGainAlbum),
            UserRating = track.UserRating,
            MusicBrainzRating = track.MusicBrainzRating,
            MusicBrainzRatingVotes = track.MusicBrainzRatingVotes,
            MusicBrainzTrackId = track.MusicBrainzTrackId,
            MusicBrainzRatingFetchedAt = track.MusicBrainzRatingFetchedAt,
            FilePath = track.PlaybackPath,
            SourcePath = track.SourcePath,
            IsFavorite = track.IsFavorite,
            ArtistId = track.ArtistId,
            AlbumId = track.AlbumId,
            EntityType = track.Source == LibraryCatalogSource.OrynivoServer ? "OrynivoTrack" : "Track",
            ExternalId = track.Source == LibraryCatalogSource.OrynivoServer
                ? track.Id.ToString(CultureInfo.InvariantCulture)
                : null,
            KnownDuration = track.KnownDuration
        };
        if (track.Source == LibraryCatalogSource.OrynivoServer && server is not null)
        {
            row.OrynivoServer = server;
            if (registerRemoteMetadata) _orynivoTracksByUrl[track.PlaybackPath] = row;
        }
        return row;
    }

    private static ContentRow ToCatalogAlbumContentRow(LibraryCatalogAlbum album, OrynivoServerSettings? server = null) => new()
    {
        Id = album.Id,
        AlbumId = album.Id,
        ArtistId = album.ArtistId,
        Title = string.IsNullOrEmpty(album.Title) ? LocalizationManager.Current.Unknown : album.Title,
        AlphabetIndexText = string.IsNullOrEmpty(album.Title) ? LocalizationManager.Current.Unknown : album.Title,
        Artist = string.IsNullOrEmpty(album.DisplayArtist) ? null : album.DisplayArtist,
        Year = album.Year?.ToString(CultureInfo.CurrentCulture),
        ArtworkPath = album.ArtworkPath,
        ThumbnailPath = album.ThumbnailPath,
        IsFavorite = album.IsFavorite,
        EntityType = album.Source == LibraryCatalogSource.OrynivoServer ? "OrynivoAlbum" : "Album",
        ExternalId = album.Source == LibraryCatalogSource.OrynivoServer
            ? album.Id.ToString(CultureInfo.InvariantCulture)
            : null,
        OrynivoServer = album.Source == LibraryCatalogSource.OrynivoServer ? server : null
    };

    private static ContentRow ToCatalogArtistContentRow(LibraryCatalogArtist artist, OrynivoServerSettings? server = null) => new()
    {
        Id = artist.Id,
        ArtistId = artist.Id,
        Title = string.IsNullOrEmpty(artist.Name) ? LocalizationManager.Current.Unknown : artist.Name,
        AlphabetIndexText = string.IsNullOrEmpty(artist.Name) ? LocalizationManager.Current.Unknown : artist.Name,
        IsFavorite = artist.IsFavorite,
        ArtworkPath = artist.ArtworkPath,
        ThumbnailPath = artist.ThumbnailPath,
        Biography = artist.Biography,
        SourceUrl = artist.SourceUrl,
        ProfileLanguage = artist.ProfileLanguage,
        ProfileFetchedAt = artist.ProfileFetchedAt,
        ImageIsManual = artist.ImageIsManual,
        EntityType = artist.Source == LibraryCatalogSource.OrynivoServer ? "OrynivoArtist" : "Artist",
        ExternalId = artist.Source == LibraryCatalogSource.OrynivoServer
            ? artist.Id.ToString(CultureInfo.InvariantCulture)
            : null,
        OrynivoServer = artist.Source == LibraryCatalogSource.OrynivoServer ? server : null,
        FilePath = ""
    };

    private static TrackListInfo ToTrackListInfo(TrackRecord track) => new(
        track.Path, track.FileName, track.Title, track.Artist, track.Album, track.AlbumArtist,
        track.Genre, track.Format, track.Bitrate, track.Duration, track.SortTitle, track.Id,
        false, track.Year, track.TrackNumber, track.TrackTotal, track.DiscNumber,
        track.DiscTotal, track.SampleRate, track.BitDepth, track.Channels, track.Composer,
        track.Bpm, track.FileSize, track.AddedAt, track.ReplayGainTrack, track.ReplayGainAlbum,
        UserRating: track.UserRating,
        MusicBrainzRating: track.MusicBrainzRating,
        MusicBrainzRatingVotes: track.MusicBrainzRatingVotes,
        MusicBrainzTrackId: track.MusicBrainzTrackId,
        MusicBrainzRatingFetchedAt: track.MusicBrainzRatingFetchedAt);

    private static string? FormatPartNumber(int? number, int? total) =>
        number is null ? null : total is > 0 ? $"{number}/{total}" : number.Value.ToString(CultureInfo.CurrentCulture);

    private static string? FormatReplayGainDisplay(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"{value} dB";

    private static string? FormatFileSize(long? bytes)
    {
        if (bytes is null || bytes < 0)
            return null;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
