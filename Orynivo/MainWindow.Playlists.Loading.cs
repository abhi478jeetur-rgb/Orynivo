using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using Orynivo.Library;
using Orynivo.Localization;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Asynchronous stored-playlist loading and guarded publication for the existing window.</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _playlistLoadCts;
    private int _playlistLoadVersion;
    private bool _playlistRetrying;

    /// <summary>Abandons pending playlist publication on navigation or source-context changes.</summary>
    private void CancelPlaylistLoad()
    {
        CancelAndDispose(ref _playlistLoadCts);
        ++_playlistLoadVersion;
        _playlistRetrying = false;
    }

    /// <summary>Loads a stored playlist without holding SQLite open during remote ID lookups.</summary>
    /// <param name="id">Stored playlist identifier.</param>
    /// <param name="cancellationToken">Owning operation cancellation.</param>
    /// <returns>Ordered rows, including non-playable unresolved entries, and explicit source outcomes.</returns>
    private async Task<LibraryLoadResult<ContentRow>> LoadStoredPlaylistAsync(long id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var capturedServers = (_settings.OrynivoServers ?? []).Select(server => new OrynivoServerSettings
            {
                Id = server.Id, Name = server.Name, BaseUrl = server.BaseUrl, ApiKey = server.ApiKey,
                ProfileId = server.ProfileId, StreamingFormat = server.StreamingFormat,
                StreamingBitrateKbps = server.StreamingBitrateKbps
            }).ToDictionary(server => server.Id, StringComparer.Ordinal);
            return (Servers: capturedServers, Favorites: new HashSet<string>(ActiveUserProfile.OrynivoServerFavorites, StringComparer.Ordinal));
        });
        var servers = context.Servers;
        var favorites = context.Favorites;
        var snapshot = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = AudioDatabase.OpenDefault();
            var playlist = db.GetPlaylistById(id);
            var entries = playlist is { IsSmartPlaylist: false } ? db.GetPlaylistTracks(id).ToArray() : [];
            var tracks = db.GetTrackListByPaths(entries.Select(entry => entry.Path));
            return (Playlist: playlist, Entries: entries, Tracks: tracks);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Playlist is { IsSmartPlaylist: true } smart)
        {
            var criteria = JsonSerializer.Deserialize<SmartPlaylistCriteria>(smart.FilterCriteria ?? "null")
                ?? throw new JsonException("Missing smart playlist criteria.");
            return await ResolveUnifiedSmartPlaylistRowsAsync(criteria, cancellationToken);
        }
        var requests = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
            if (PlaylistReferences.TryParseTrack(entry.Path, out var source, out var trackId))
            {
                if (!requests.TryGetValue(source, out var ids)) requests[source] = ids = [];
                ids.Add(trackId);
            }
        var remote = await PlaylistTrackBatchLoader.LoadAsync(
            requests.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<long>)pair.Value, StringComparer.Ordinal),
            async (source, ids, token) =>
            {
                var server = servers[source];
                var provider = new OrynivoServerLibraryCatalogProvider(server, _orynivoClient,
                    (entity, trackId) => favorites.Contains(GetOrynivoFavoriteKey(server.Id, entity, trackId)), requireComplete: true);
                var tracks = await provider.GetTracksByIdsAsync(ids, token);
                return (IReadOnlyList<(string Source, LibraryCatalogTrack Track)>)tracks
                    .Where(track => ids.Contains(track.Id)).Select(track => (source, track)).ToArray();
            }, cancellationToken, timeout: TimeSpan.FromSeconds(20));
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            var localByPath = snapshot.Tracks.ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
            var remoteById = remote.Rows.GroupBy(row => (row.Source, row.Track.Id))
                .ToDictionary(group => group.Key, group => group.First().Track);
            var rows = new List<ContentRow>(snapshot.Entries.Length);
            foreach (var entry in snapshot.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ContentRow row;
                if (localByPath.TryGetValue(entry.Path, out var local))
                    row = ToTrackContentRow(local);
                else if (PlaylistReferences.TryParseTrack(entry.Path, out var source, out var trackId) &&
                    remoteById.TryGetValue((source, trackId), out var track))
                    row = ToCatalogTrackContentRow(track, servers[source], registerRemoteMetadata: false);
                else if (IsHttpUrl(entry.Path) && CanPersistQueuePath(entry.Path))
                    row = new ContentRow { Title = LocalizationManager.Current.InternetRadio,
                        FilePath = entry.Path, EntityType = "Track" };
                else
                    row = new ContentRow { Title = LocalizationManager.Current.PlaylistTrackUnavailable,
                        EntityType = "MissingPlaylistTrack" };
                if (row.EntityType == "MissingPlaylistTrack" &&
                    PlaylistReferences.TryParseTrack(entry.Path, out var missingSource, out _))
                {
                    row.OrynivoServer = servers.GetValueOrDefault(missingSource);
                    row.UnresolvedPlaylistServerId = missingSource;
                }
                row.Nr = (rows.Count + 1).ToString(CultureInfo.CurrentCulture);
                row.PlaylistEntryId = entry.Id;
                rows.Add(row);
            }
            var outcomes = requests.Keys.Select(source =>
            {
                var statuses = remote.Sources.Where(outcome => string.Equals(
                        outcome.SourceKey[..outcome.SourceKey.LastIndexOf(":batch:", StringComparison.Ordinal)], source, StringComparison.Ordinal))
                    .Select(outcome => outcome.Status).ToArray();
                var status = statuses.All(status => status == LibrarySourceLoadStatus.Success)
                    ? LibrarySourceLoadStatus.Success
                    : statuses.Contains(LibrarySourceLoadStatus.TimedOut) ? LibrarySourceLoadStatus.TimedOut : LibrarySourceLoadStatus.Failed;
                return new LibrarySourceLoadOutcome(GetServerSourceKey(source), status);
            }).Prepend(new LibrarySourceLoadOutcome("local", LibrarySourceLoadStatus.Success)).ToArray();
            return new LibraryLoadResult<ContentRow>(rows, outcomes);
        }, cancellationToken);
    }

    /// <summary>Binds only a current playlist load and registers remote playback metadata on the dispatcher.</summary>
    /// <param name="id">Stored playlist identifier.</param>
    /// <param name="isRetry">Whether usable rows and selection should remain visible.</param>
    /// <returns>A task completing after guarded publication or cancellation.</returns>
    private async Task ShowStoredPlaylistAsync(long id, bool isRetry = false)
    {
        CancelPlaylistLoad();
        _playlistLoadCts = new CancellationTokenSource();
        var token = _playlistLoadCts.Token;
        var version = _playlistLoadVersion;
        var generation = _unifiedLibraryViewCache.Generation;
        _playlistRetrying = isRetry;
        if (!isRetry) ContentDataGrid.ItemsSource = null;
        UpdateUnifiedLibraryLoadNotice();
        try
        {
            var result = await LoadStoredPlaylistAsync(id, token);
            if (token.IsCancellationRequested || version != _playlistLoadVersion ||
                generation != _unifiedLibraryViewCache.Generation || _activePlaylistId != id)
                return;
            var selection = isRetry ? ContentDataGrid.SelectedItem as ContentRow : null;
            var offset = isRetry ? CaptureCurrentVerticalOffset() : null;
            foreach (var row in result.Rows)
                if (row.OrynivoServer is not null && !string.IsNullOrEmpty(row.FilePath))
                    _orynivoTracksByUrl[row.FilePath] = row;
            _unifiedLibraryLoadResult = result;
            ApplyColumns($"Playlist:{id}");
            ContentDataGrid.ItemsSource = result.Rows;
            if (selection is not null)
                ContentDataGrid.SelectedItem = result.Rows.FirstOrDefault(row => selection.PlaylistEntryId.HasValue
                    ? row.PlaylistEntryId == selection.PlaylistEntryId
                    : row.Id == selection.Id && row.SourceKey == selection.SourceKey);
            if (offset.HasValue)
                RestoreDataGridPositionAfterLayout(ContentDataGrid.SelectedItem as ContentRow, offset,
                    () => version == _playlistLoadVersion && generation == _unifiedLibraryViewCache.Generation && _activePlaylistId == id);
            UpdateAlphabetIndex(result.Rows, false);
            ContentCountTextBlock.Text = LocalizationManager.FormatEntryCount(result.Rows.Count);
            UpdateUnifiedLibraryLoadNotice();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (!token.IsCancellationRequested && version == _playlistLoadVersion &&
                generation == _unifiedLibraryViewCache.Generation && _activePlaylistId == id)
            {
                _unifiedLibraryLoadResult = new([], [new("local", LibrarySourceLoadStatus.Failed)]);
                UpdateUnifiedLibraryLoadNotice();
            }
        }
        finally
        {
            if (version == _playlistLoadVersion)
            {
                _playlistRetrying = false;
                UpdateUnifiedLibraryLoadNotice();
            }
        }
    }

    /// <summary>Updates unresolved entry labels in place when the interface language changes.</summary>
    private void RefreshPlaylistPlaceholderText()
    {
        if (_activePlaylistId is null || ContentDataGrid.ItemsSource is not IEnumerable<ContentRow> rows) return;
        foreach (var row in rows.Where(row => row.EntityType == "MissingPlaylistTrack"))
            row.Title = LocalizationManager.Current.PlaylistTrackUnavailable;
    }
}
