using System.Text.Json;
using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Sanitized restart previews for Dashboard sections and Genre Cloud levels.</summary>
public partial class MainWindow
{
    private static readonly string[] PreviewRowFields =
    [
        nameof(ContentRow.Nr), nameof(ContentRow.Id), nameof(ContentRow.ArtistId), nameof(ContentRow.AlbumId),
        nameof(ContentRow.Title), nameof(ContentRow.AlphabetIndexText), nameof(ContentRow.Artist), nameof(ContentRow.Album),
        nameof(ContentRow.AlbumArtist), nameof(ContentRow.Year), nameof(ContentRow.TrackNumber), nameof(ContentRow.DiscNumber),
        nameof(ContentRow.Genre), nameof(ContentRow.Bitrate), nameof(ContentRow.SampleRate), nameof(ContentRow.SampleRateHz),
        nameof(ContentRow.BitDepth), nameof(ContentRow.Channels), nameof(ContentRow.ChannelCount), nameof(ContentRow.Composer),
        nameof(ContentRow.Bpm), nameof(ContentRow.CamelotKey), nameof(ContentRow.FileName), nameof(ContentRow.FileSize),
        nameof(ContentRow.AddedAt), nameof(ContentRow.ReplayGainTrack), nameof(ContentRow.ReplayGainAlbum),
        nameof(ContentRow.UserRating), nameof(ContentRow.MusicBrainzRating), nameof(ContentRow.MusicBrainzRatingVotes),
        nameof(ContentRow.MusicBrainzTrackId), nameof(ContentRow.MusicBrainzRatingFetchedAt), nameof(ContentRow.Folder),
        nameof(ContentRow.ArtworkPath), nameof(ContentRow.ThumbnailPath),
        nameof(ContentRow.EntityType), nameof(ContentRow.ExternalId), nameof(ContentRow.Duration), nameof(ContentRow.Format),
        nameof(ContentRow.FilePath), nameof(ContentRow.SourcePath), nameof(ContentRow.KnownDuration), nameof(ContentRow.IsFavorite)
    ];
    private readonly ViewPreviewStore _viewPreviews = new(AppPaths.DataRoot);
    private int _previewConfigurationVersion;
    private Task _previewResetTask = Task.CompletedTask;
    private Task _previewWriteTask = Task.CompletedTask;

    /// <summary>Builds a stable profile/source identity without including authentication secrets.</summary>
    /// <returns>A digest used only to select a preview file.</returns>
    private string ViewPreviewIdentity() => ViewPreviewStore.Fingerprint(new
    {
        _settings.ActiveUserProfileId,
        Roots = _settings.LibraryPaths,
        Sources = (_settings.OrynivoServers ?? []).Select(server => new
        {
            server.Id,
            server.BaseUrl,
            server.ProfileId,
            server.StreamingFormat,
            server.StreamingBitrateKbps
        }).OrderBy(server => server.Id, StringComparer.Ordinal).ToArray()
    });

    /// <summary>Discards restart previews and rejects older writes after source/profile changes.</summary>
    private void ClearViewPreviews()
    {
        Interlocked.Increment(ref _previewConfigurationVersion);
        _previewResetTask = _viewPreviews.ClearAsync();
    }

    /// <summary>Waits for configuration reset before reading optional restart data.</summary>
    /// <typeparam name="T">Sanitized preview type.</typeparam>
    /// <param name="identity">Captured profile/source identity.</param>
    /// <param name="section">Captured section/options key.</param>
    /// <returns>Available display data, or null.</returns>
    private async Task<T?> ReadViewPreviewAsync<T>(string identity, string section) where T : class
    {
        await _previewResetTask;
        return await _viewPreviews.ReadAsync<T>(identity, section);
    }

    /// <summary>Schedules bounded optional persistence for the original configuration.</summary>
    /// <typeparam name="T">Sanitized preview type.</typeparam>
    /// <param name="identity">Captured profile/source identity.</param>
    /// <param name="section">Captured section/options key.</param>
    /// <param name="preview">Sanitized display snapshot.</param>
    /// <param name="version">Configuration ownership captured before loading.</param>
    private void SaveViewPreview<T>(string identity, string section, T preview, int version)
    {
        var write = _viewPreviews.WriteAsync(identity, section, preview,
            () => version == Volatile.Read(ref _previewConfigurationVersion));
        _previewWriteTask = _previewWriteTask.IsCompleted ? write : Task.WhenAll(_previewWriteTask, write);
    }

    /// <summary>Portable album display data; remote connections and artwork URLs are reconstructed at runtime.</summary>
    /// <param name="Id">Provider-local album identity.</param>
    /// <param name="Title">Album display title.</param>
    /// <param name="Artist">Album display artist.</param>
    /// <param name="ArtistId">Provider-local artist identity.</param>
    /// <param name="AddedAt">Recorded addition timestamp.</param>
    /// <param name="SourceId">Opaque server identity, or null for local albums.</param>
    /// <param name="LocalArtwork">Safe local artwork path.</param>
    /// <param name="HasArtwork">Whether artwork was available.</param>
    /// <param name="IsFavorite">Recorded personal favorite state.</param>
    /// <param name="AlbumIds">Provider-local logical album identities.</param>
    /// <param name="Parts">Source-aware logical album parts.</param>
    private sealed record PreviewAlbum(long Id, string Title, string Artist, long? ArtistId,
        long AddedAt, string? SourceId, string? LocalArtwork, bool HasArtwork, bool IsFavorite,
        IReadOnlyList<long>? AlbumIds, List<PreviewAlbumPart>? Parts);

    /// <summary>Opaque provider-local logical album identity.</summary>
    /// <param name="AlbumId">Provider-local album identity.</param>
    /// <param name="ArtistId">Provider-local artist identity.</param>
    /// <param name="SourceId">Opaque server identity, or null for local albums.</param>
    private sealed record PreviewAlbumPart(long AlbumId, long? ArtistId, string? SourceId);

    /// <summary>Sanitized history and recent-album strip inputs.</summary>
    /// <param name="History">History entries with unsafe paths removed.</param>
    /// <param name="Thumbs">Safe local thumbnail paths.</param>
    /// <param name="Favorites">Personal favorite track identities.</param>
    /// <param name="Albums">Portable recent albums.</param>
    private sealed record PreviewMedia(List<DailyHistoryEntry> History, Dictionary<long, string> Thumbs,
        HashSet<long> Favorites, List<PreviewAlbum> Albums);

    /// <summary>Portable listening and calendar data without authenticated history paths.</summary>
    /// <param name="Calendar">Daily aggregates for the selected month.</param>
    /// <param name="Genres">Genre listening aggregates.</param>
    /// <param name="Albums">Album listening aggregates.</param>
    /// <param name="Artists">Artist listening aggregates.</param>
    /// <param name="Total">Current-period listening seconds.</param>
    /// <param name="Previous">Previous-period listening seconds.</param>
    /// <param name="Trend">Selected-period trend samples.</param>
    private sealed record PreviewStats(List<CalendarDayData> Calendar, List<(string Genre, double Seconds)> Genres,
        List<TopAlbumStat> Albums, List<TopArtistStat> Artists, double Total, double Previous,
        IReadOnlyList<double> Trend);

    /// <summary>Portable scalar recommendation row with an opaque owning server identity.</summary>
    /// <param name="SourceId">Opaque owning server identity.</param>
    /// <param name="Values">Explicitly allowed scalar display values.</param>
    private sealed record PreviewRow(string? SourceId, Dictionary<string, JsonElement> Values);

    /// <summary>Restart cloud preview without credentials, URLs or bitmap data.</summary>
    /// <param name="Nodes">Merged taxonomy nodes.</param>
    /// <param name="Tracks">Portable ranked tracks.</param>
    /// <param name="Albums">Portable album recommendations.</param>
    private sealed record PreviewCloud(List<GenreCloudNode> Nodes, List<PreviewRow> Tracks, List<PreviewRow> Albums);

    /// <summary>Removes any authenticated URL from a persisted optional path.</summary>
    /// <param name="path">Optional local path or remote address.</param>
    /// <returns>The safe path, or null.</returns>
    private static string? PreviewSafePath(string? path) =>
        path is not null && QueuePathPolicy.CanPersist(path) ? path : null;

    /// <summary>Restores the four Dashboard data sections before any remote refresh is awaited.</summary>
    /// <param name="identity">Captured source/profile identity.</param>
    /// <param name="statsKey">Captured calendar and listening options.</param>
    /// <param name="recommendationKey">Captured recommendation options.</param>
    /// <param name="canPublish">Original navigation/generation guard.</param>
    /// <returns>A task completing after optional cache publication.</returns>
    private async Task RestoreDashboardPreviewAsync(string identity, string statsKey,
        string recommendationKey, Func<bool> canPublish)
    {
        if (_dashboardSections.Skip(1).All(section => section.Content is not null)) return;
        var summaryTask = ReadViewPreviewAsync<DashboardLibrarySummary>(identity, "dashboard-summary");
        var mediaTask = ReadViewPreviewAsync<PreviewMedia>(identity, "dashboard-media");
        var statsTask = ReadViewPreviewAsync<PreviewStats>(identity, statsKey);
        var recommendationsTask = ReadViewPreviewAsync<List<PreviewAlbum>>(identity, recommendationKey);
        await Task.WhenAll(summaryTask, mediaTask, statsTask, recommendationsTask);
        if (!canPublish()) return;
        if (summaryTask.Result is { } summary)
        {
            _dashboardLastSummary = summary;
            PublishDashboardSection(0, new { Summary = summary, Hour = DateTime.Now.Hour }, () => DashboardBuildGreeting(summary));
        }
        if (mediaTask.Result is { } media && _dashboardSections[1].Content is null)
        {
            var entries = media.History.Select(entry => TryGetOrynivoHistoryTarget(entry, out var server, out var id)
                ? entry with { Path = OrynivoServerClient.GetStreamUrl(server, id) } : entry).ToList();
            var history = (Entries: entries, Thumbs: media.Thumbs, FavoriteTrackIds: media.Favorites);
            var albums = RestorePreviewAlbums(media.Albums);
            PublishDashboardSection(1, new { history, Albums = DashboardAlbumDisplayInputs(albums) }, () =>
                DashboardBuildMediaOverview(entries, media.Thumbs, media.Favorites, albums));
        }
        if (recommendationsTask.Result is { } recommendations && _dashboardSections[2].Content is null)
        {
            var albums = RestorePreviewAlbums(recommendations);
            PublishDashboardSection(2, new
            {
                Albums = DashboardAlbumDisplayInputs(albums),
                _dashboardRecommendationPeriod,
                _dashboardRecommendationMood,
                _settings.DashboardRecommendationStageView
            },
                () => DashboardBuildRecommendations(albums));
        }
        if (statsTask.Result is { } stats && _dashboardSections[3].Content is null)
            PublishDashboardSection(3, new
            {
                stats,
                period = _dashboardStatsPeriod,
                year = _dashboardYear,
                month = _dashboardMonth,
                Summary = _dashboardLastSummary
            }, () =>
                DashboardBuildStatsSection(stats.Calendar, stats.Genres, stats.Albums, stats.Artists,
                    stats.Total, stats.Previous, stats.Trend, _dashboardLastSummary));
    }

    /// <summary>Converts album display data to opaque source references.</summary>
    /// <param name="albums">Current runtime album data.</param>
    /// <returns>Credential-free restart data.</returns>
    private static List<PreviewAlbum> SavePreviewAlbums(List<DashboardAlbum> albums) => albums.Select(album =>
        new PreviewAlbum(album.Id, album.Title, album.Artist, album.ArtistId, album.AddedAt,
            album.Server?.Id, album.Server is null ? PreviewSafePath(album.ArtworkPath) : null,
            album.HasArtwork, album.IsFavorite, album.LogicalAlbumIds?.ToArray(),
            album.LogicalAlbumParts?.Select(part => new PreviewAlbumPart(part.AlbumId, part.ArtistId, part.Server?.Id)).ToList())).ToList();

    /// <summary>Restores albums only from currently configured source identities.</summary>
    /// <param name="albums">Portable album display data.</param>
    /// <returns>Runtime cards with freshly reconstructed authenticated artwork.</returns>
    private List<DashboardAlbum> RestorePreviewAlbums(List<PreviewAlbum> albums) => albums
        .Where(album => album.SourceId is null || (_settings.OrynivoServers ?? []).Any(server => server.Id == album.SourceId))
        .Select(album =>
        {
            var server = (_settings.OrynivoServers ?? []).FirstOrDefault(source => source.Id == album.SourceId);
            return new DashboardAlbum(album.Id, album.Title, album.Artist, album.ArtistId, album.AddedAt,
                server, server is null ? album.LocalArtwork : album.HasArtwork
                    ? OrynivoServerClient.GetAlbumArtworkUrl(server, album.Id, 320) : null,
                album.HasArtwork, album.IsFavorite, album.AlbumIds,
                album.Parts?.Select(part => new LogicalAlbumPart(part.AlbumId, part.ArtistId,
                    (_settings.OrynivoServers ?? []).FirstOrDefault(source => source.Id == part.SourceId))).ToList());
        }).ToList();

    /// <summary>Copies only writable scalar row data, excluding all remote paths and connection objects.</summary>
    /// <param name="rows">Runtime recommendation rows.</param>
    /// <returns>Credential-free row previews.</returns>
    private static List<PreviewRow> SavePreviewRows(List<ContentRow> rows) => rows.Select(row =>
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in PreviewRowFields)
        {
            var property = typeof(ContentRow).GetProperty(field)!;
            var value = property.GetValue(row);
            if (value is string text &&
                (property.Name is "FilePath" or "SourcePath" or "ArtworkPath" or "ThumbnailPath" or "SourceUrl"))
                value = row.OrynivoServer is null ? PreviewSafePath(text) : null;
            values[property.Name] = JsonSerializer.SerializeToElement(value, property.PropertyType);
        }
        return new PreviewRow(row.OrynivoServer?.Id, values);
    }).ToList();

    /// <summary>Restores scalar rows and reconstructs remote playback/artwork from current settings.</summary>
    /// <param name="rows">Portable recommendation rows.</param>
    /// <returns>Current-source rows without registering playback metadata.</returns>
    private List<ContentRow> RestorePreviewRows(List<PreviewRow> rows) => rows.Select(preview =>
    {
        var server = (_settings.OrynivoServers ?? []).FirstOrDefault(source => source.Id == preview.SourceId);
        if (preview.SourceId is not null && server is null) return null;
        var values = new Dictionary<string, JsonElement>(preview.Values);
        if (server is not null && values.TryGetValue(nameof(ContentRow.Id), out var idValue) && idValue.TryGetInt64(out var id))
        {
            if (values.TryGetValue(nameof(ContentRow.EntityType), out var kind) && kind.GetString() == "OrynivoTrack")
            {
                values[nameof(ContentRow.FilePath)] = JsonSerializer.SerializeToElement(OrynivoServerClient.GetStreamUrl(server, id));
                values[nameof(ContentRow.ArtworkPath)] = JsonSerializer.SerializeToElement(OrynivoServerClient.GetTrackArtworkUrl(server, id, 320));
                values[nameof(ContentRow.ThumbnailPath)] = JsonSerializer.SerializeToElement(OrynivoServerClient.GetTrackArtworkUrl(server, id, 96));
            }
            else if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "OrynivoAlbum")
                values[nameof(ContentRow.ArtworkPath)] = JsonSerializer.SerializeToElement(OrynivoServerClient.GetAlbumArtworkUrl(server, id, 320));
        }
        var row = JsonSerializer.Deserialize<ContentRow>(JsonSerializer.Serialize(values));
        if (row is not null) row.OrynivoServer = server;
        return row;
    }).Where(row => row is not null).Cast<ContentRow>().ToList();
}
