using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Bounded asynchronous smart-playlist resolution shared by browsing and count previews.</summary>
public partial class MainWindow
{
    /// <summary>Counts complete smart-playlist results without publishing partial counts.</summary>
    /// <param name="criteria">Current editor criteria.</param>
    /// <param name="cancellationToken">Preview cancellation.</param>
    /// <returns>The complete count, or null when source data or its owning context changed.</returns>
    private async Task<int?> ResolveUnifiedSmartPlaylistCountAsync(
        SmartPlaylistCriteria criteria, CancellationToken cancellationToken)
    {
        var generation = _unifiedLibraryLoads.Generation;
        var resolved = await LoadUnifiedSmartPlaylistAsync(criteria, cancellationToken, composeRows: false);
        return generation == _unifiedLibraryLoads.Generation && resolved.Result.Status == LibraryLoadStatus.Complete
            ? resolved.Count : null;
    }

    /// <summary>Captures source context, resolves complete catalogs and features, and composes rows off the dispatcher.</summary>
    /// <param name="criteria">Persisted criteria, unchanged by transient identity projection.</param>
    /// <param name="cancellationToken">Owning browse or preview cancellation.</param>
    /// <returns>Available rows and explicit source outcomes without playback registration.</returns>
    private async Task<LibraryLoadResult<ContentRow>> ResolveUnifiedSmartPlaylistRowsAsync(
        SmartPlaylistCriteria criteria, CancellationToken cancellationToken)
        => (await LoadUnifiedSmartPlaylistAsync(criteria, cancellationToken, composeRows: true)).Result;

    /// <summary>Resolves captured criteria without hydrating rows when only a count is needed.</summary>
    /// <param name="criteria">Persisted criteria to capture.</param>
    /// <param name="cancellationToken">Owning operation cancellation.</param>
    /// <param name="composeRows">Whether matched track metadata must be hydrated for browsing.</param>
    /// <returns>Available rows, source outcomes, and the criteria match count.</returns>
    private async Task<(LibraryLoadResult<ContentRow> Result, int Count)> LoadUnifiedSmartPlaylistAsync(
        SmartPlaylistCriteria criteria, CancellationToken cancellationToken, bool composeRows)
    {
        cancellationToken.ThrowIfCancellationRequested();
        criteria = JsonSerializer.Deserialize<SmartPlaylistCriteria>(JsonSerializer.Serialize(criteria))!;
        var context = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var servers = (_settings.OrynivoServers ?? []).Select(server => new OrynivoServerSettings
            {
                Id = server.Id, Name = server.Name, BaseUrl = server.BaseUrl, ApiKey = server.ApiKey,
                ProfileId = server.ProfileId, StreamingFormat = server.StreamingFormat,
                StreamingBitrateKbps = server.StreamingBitrateKbps
            }).ToArray();
            return (Servers: servers, Favorites: new HashSet<string>(ActiveUserProfile.OrynivoServerFavorites, StringComparer.Ordinal));
        });
        var similarity = !string.IsNullOrWhiteSpace(criteria.SimilaritySourceKey) && criteria.SimilarityTrackId.HasValue;
        var seedSource = criteria.SimilaritySourceKey;
        if (seedSource?.StartsWith("orynivo:", StringComparison.OrdinalIgnoreCase) == true)
            seedSource = "server:" + seedSource["orynivo:".Length..];
        var selectedSources = new HashSet<string>(criteria.SourceKeys, StringComparer.OrdinalIgnoreCase);
        bool NeedsSource(string source) => selectedSources.Count == 0 || selectedSources.Contains(source)
            || (similarity && string.Equals(source, seedSource, StringComparison.OrdinalIgnoreCase));
        var localTask = NeedsSource("local") ? LibrarySourceLoader.LoadAsync<SmartPlaylistSourceData>("local", token => Task.Run<IReadOnlyList<SmartPlaylistSourceData>>(() =>
        {
            token.ThrowIfCancellationRequested();
            using var db = AudioDatabase.OpenDefault();
            var candidates = db.GetSmartPlaylistTracks();
            var features = similarity ? db.GetSimilarityTrackProfiles().Select(SimilarityFeatureService.Create).ToList() : [];
            token.ThrowIfCancellationRequested();
            return [new(null, [], candidates, features)];
        }, token), cancellationToken) : Task.FromResult(new LibraryLoadResult<SmartPlaylistSourceData>([], []));
        // Providers map large pages and reapply favorites after awaits; keep those continuations off the dispatcher too.
        var remoteTask = Task.Run(() => LibrarySourceBatchLoader.LoadAsync<OrynivoServerSettings, SmartPlaylistSourceData>(
            context.Servers.Where(server => NeedsSource(GetServerSourceKey(server.Id))).ToArray(),
            server => GetServerSourceKey(server.Id), async (server, token) =>
            {
                var tracks = await LoadAllOrynivoTracksAsync(server, token, context.Favorites);
                var features = similarity ? await LibraryCatalogPaging.LoadAllAsync<SimilarityFeatureVector>(
                    async (page, pageToken) => await _orynivoClient.GetSimilarityFeaturesAsync(
                        server, page, SimilarityPageSize, pageToken, requireComplete: true),
                    SimilarityPageSize, token) : [];
                return (IReadOnlyList<SmartPlaylistSourceData>)[new(server, tracks, [], features)];
            }, cancellationToken, timeout: TimeSpan.FromSeconds(60)), cancellationToken);
        await Task.WhenAll(localTask, remoteTask);
        cancellationToken.ThrowIfCancellationRequested();
        var data = LibraryLoadResult<SmartPlaylistSourceData>.Combine([await localTask, await remoteTask]);
        var knownSources = new HashSet<string>(context.Servers.Select(server => GetServerSourceKey(server.Id)), StringComparer.OrdinalIgnoreCase) { "local" };
        var requestedSources = selectedSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (similarity && seedSource is not null) requestedSources.Add(seedSource);
        var missingSources = requestedSources.Where(source => !knownSources.Contains(source))
            .Select(_ => new LibrarySourceLoadOutcome("unconfigured", LibrarySourceLoadStatus.Failed)).ToArray();
        data = data with { Sources = data.Sources.Concat(missingSources).ToArray() };
        return await Task.Run(() =>
        {
            var candidates = new List<SmartPlaylistTrackInfo>();
            var features = new List<SimilarityFeatureVector>();
            var remoteIds = new Dictionary<(string Source, long Id), long>();
            var remoteTracks = new Dictionary<long, (OrynivoServerSettings Server, LibraryCatalogTrack Track)>();
            long nextId = -1;
            foreach (var source in data.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                features.AddRange(source.Features);
                if (source.Server is null)
                {
                    candidates.AddRange(source.Candidates);
                    continue;
                }
                var sourceKey = GetServerSourceKey(source.Server.Id);
                foreach (var track in source.Tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = nextId--;
                    remoteIds[(sourceKey, track.Id)] = id;
                    candidates.Add(ToSmartPlaylistCandidate(id, track, sourceKey));
                    remoteTracks[id] = (source.Server, track);
                }
            }
            var resolved = SmartPlaylistSimilarityProjection.Resolve(criteria, candidates, features, remoteIds);
            cancellationToken.ThrowIfCancellationRequested();
            if (!composeRows)
                return (new LibraryLoadResult<ContentRow>([], data.Sources), resolved.Count);
            var localRows = new Dictionary<long, ContentRow>();
            var outcomes = data.Sources.ToList();
            var localIds = resolved.Where(candidate => candidate.Id > 0).Select(candidate => candidate.Id).ToList();
            if (localIds.Count > 0)
                try
                {
                    using var db = AudioDatabase.OpenDefault();
                    foreach (var track in db.GetTrackListByIds(localIds))
                        localRows[track.Id] = ToTrackContentRow(track);
                }
                catch
                {
                    outcomes.RemoveAll(outcome => outcome.SourceKey == "local");
                    outcomes.Add(new("local", LibrarySourceLoadStatus.Failed));
                }
            var rows = new List<ContentRow>(resolved.Count);
            foreach (var candidate in resolved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ContentRow? row = null;
                if (candidate.Id > 0) localRows.TryGetValue(candidate.Id, out row);
                else if (remoteTracks.TryGetValue(candidate.Id, out var remote))
                    row = ToCatalogTrackContentRow(remote.Track, remote.Server, registerRemoteMetadata: false);
                if (row is null) continue;
                row.Nr = (rows.Count + 1).ToString(CultureInfo.CurrentCulture);
                rows.Add(row);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (new LibraryLoadResult<ContentRow>(rows, outcomes), resolved.Count);
        }, cancellationToken);
    }

    /// <summary>Holds one successful source snapshot without retaining an open database.</summary>
    /// <param name="Server">Captured remote context, or null for local data.</param>
    /// <param name="Tracks">Compact remote track catalog.</param>
    /// <param name="Candidates">Compact local criteria candidates.</param>
    /// <param name="Features">Provider-local similarity vectors.</param>
    private sealed record SmartPlaylistSourceData(OrynivoServerSettings? Server,
        IReadOnlyList<LibraryCatalogTrack> Tracks, List<SmartPlaylistTrackInfo> Candidates,
        IReadOnlyList<SimilarityFeatureVector> Features);
}
