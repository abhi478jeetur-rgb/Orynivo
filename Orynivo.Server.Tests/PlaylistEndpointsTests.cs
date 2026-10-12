using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orynivo.Library;
using Orynivo.Server.Endpoints;
using Orynivo.Server.Middleware;
using Xunit;

namespace Orynivo.Server.Tests;

/// <summary>Exercises production playlist routes with real middleware and isolated temporary SQLite.</summary>
public sealed class PlaylistEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orynivo-playlist-api-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _trackId;
    private long[] _ids = [];
    private int _connections;
    private string DatabasePath => Path.Combine(_root, "library.db");

    /// <summary>Seeds synthetic tracks and starts only the production route/middleware subset in memory.</summary>
    /// <returns>The host startup task.</returns>
    public async Task InitializeAsync()
    {
        using (var db = new AudioDatabase(DatabasePath))
        {
            for (var index = 0; index < 3; index++)
                db.Upsert(new TrackRecord
                {
                    Path = Path.Combine(_root, $"track-{index}.flac"),
                    FileName = $"track-{index}.flac",
                    Title = $"Track {index:D2}",
                    Artist = "Synthetic artist",
                    Album = "Synthetic album",
                    Genre = index < 2 ? "Rock" : "Ambient",
                    Bpm = index < 2 ? 120 : 60,
                    Format = "flac"
                });
            _ids = db.GetTrackListPage(0, 3).Select(track => track.Id).ToArray();
            _trackId = _ids[0];
            var previousProfile = AudioDatabase.ActiveProfileId;
            try { AudioDatabase.SetActiveProfile("alpha"); db.SetTrackUserRating(_trackId, 4); db.SetTrackFavorite(_trackId, true); }
            finally { AudioDatabase.SetActiveProfile(previousProfile); }
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ServerSettings
        {
            ApiKey = "synthetic-playlist-key",
            Profiles = [new ServerProfile(), new ServerProfile { Id = "alpha", Name = "Alpha" },
                new ServerProfile { Id = "beta", Name = "Beta" }]
        });
        _app = builder.Build();
        _app.UseMiddleware<ApiKeyMiddleware>();
        _app.UseMiddleware<ProfileContextMiddleware>();
        _app.MapPlaylistCatalogEndpoints(() => { Interlocked.Increment(ref _connections); return new AudioDatabase(DatabasePath); });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    /// <summary>Stops the in-memory host and deletes only this fixture's temporary database after releasing its pool.</summary>
    /// <returns>The host shutdown task.</returns>
    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Regular playlists preserve order and repeated entries while omitting unknown input IDs.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task Regular_PreservesOrderAndDuplicates()
    {
        var playlist = await CreateAsync(new[] { _ids[1], _ids[0], _ids[1], long.MaxValue });
        var rows = await ReadAsync($"/api/playlists/{playlist}/tracks");
        Assert.Equal(new[] { _ids[1], _ids[0], _ids[1] }, rows.EnumerateArray().Select(row => row.GetProperty("track").GetProperty("id").GetInt64()));
        Assert.Equal(new[] { 1, 2, 3 }, rows.EnumerateArray().Select(row => row.GetProperty("position").GetInt32()));
        Assert.Equal(3, rows.EnumerateArray().Select(row => row.GetProperty("playlistEntryId").GetInt64()).Distinct().Count());
    }

    /// <summary>Shared playlists materialize each request's personal track values independently.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task SharedPlaylist_ReadsPersonalStatePerProfile()
    {
        var playlist = await CreateAsync([_trackId], "alpha");
        var reads = await Task.WhenAll(ReadAsync($"/api/playlists/{playlist}/tracks", "alpha"),
            ReadAsync($"/api/playlists/{playlist}/tracks", "beta"));
        Assert.True(reads[0][0].GetProperty("track").GetProperty("isFavorite").GetBoolean());
        Assert.Equal(4, reads[0][0].GetProperty("track").GetProperty("userRating").GetInt32());
        Assert.False(reads[1][0].GetProperty("track").GetProperty("isFavorite").GetBoolean());
        Assert.Equal(0, reads[1][0].GetProperty("track").GetProperty("userRating").GetInt32());
        Assert.Contains((await ReadAsync("/api/playlists", "beta")).EnumerateArray(), row => row.GetProperty("id").GetInt64() == playlist);
    }

    /// <summary>Append, entry removal and playlist deletion use the real mutation routes.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task Regular_MutationsAndMissingIds()
    {
        var playlist = await CreateAsync([]);
        Assert.Empty((await ReadAsync($"/api/playlists/{playlist}/tracks")).EnumerateArray());
        await SendAsync(HttpMethod.Post, $"/api/playlists/{playlist}/tracks", new { trackIds = new[] { _ids[0], _ids[1] } }, HttpStatusCode.NoContent);
        var rows = await ReadAsync($"/api/playlists/{playlist}/tracks");
        await SendAsync(HttpMethod.Delete, $"/api/playlist-tracks/{rows[0].GetProperty("playlistEntryId").GetInt64()}", null, HttpStatusCode.NoContent);
        Assert.Equal(_ids[1], (await ReadAsync($"/api/playlists/{playlist}/tracks"))[0].GetProperty("track").GetProperty("id").GetInt64());
        await SendAsync(HttpMethod.Delete, $"/api/playlists/{playlist}", null, HttpStatusCode.NoContent);
        await SendAsync(HttpMethod.Get, $"/api/playlists/{playlist}/tracks", null, HttpStatusCode.NotFound);
        await SendAsync(HttpMethod.Delete, $"/api/playlists/{playlist}", null, HttpStatusCode.NotFound);
        await SendAsync(HttpMethod.Post, "/api/playlists/9223372036854775807/tracks", new { trackIds = _ids }, HttpStatusCode.NotFound);
    }

    /// <summary>Client favorite overrides agree between smart resolution and count while normal reads retain profile state.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task Smart_FavoriteOverrideMatchesCount()
    {
        var criteria = JsonSerializer.Serialize(new SmartPlaylistCriteria { FavoritesOnly = true });
        var playlist = await CreateSmartAsync(criteria);
        var resolved = await SendAsync(HttpMethod.Post, $"/api/playlists/{playlist}/resolve", new { favoriteTrackIds = new[] { _ids[1], long.MaxValue } }, HttpStatusCode.OK, "beta");
        Assert.Equal(_ids[1], Assert.Single(resolved.EnumerateArray()).GetProperty("track").GetProperty("id").GetInt64());
        var count = await SendAsync(HttpMethod.Post, "/api/playlists/resolve-count", new { criteria, favoriteTrackIds = new[] { _ids[1], long.MaxValue } }, HttpStatusCode.OK, "beta");
        Assert.Equal(1, count.GetProperty("count").GetInt32());
        Assert.Equal(_ids[0], Assert.Single((await ReadAsync($"/api/playlists/{playlist}/tracks", "alpha")).EnumerateArray()).GetProperty("track").GetProperty("id").GetInt64());
        Assert.Empty((await ReadAsync($"/api/playlists/{playlist}/tracks", "beta")).EnumerateArray());
        await SendAsync(HttpMethod.Post, $"/api/playlists/{playlist}/tracks", new { trackIds = _ids }, HttpStatusCode.NotFound);
    }

    /// <summary>Similarity resolution uses the production vector resolver and excludes its reference.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task Smart_SimilarityResolvesNeighbour()
    {
        var criteria = JsonSerializer.Serialize(new SmartPlaylistCriteria { SimilaritySourceKey = "local", SimilarityTrackId = _ids[0] });
        var playlist = await CreateSmartAsync(criteria);
        var resolved = await ReadAsync($"/api/playlists/{playlist}/tracks");
        var ids = resolved.EnumerateArray().Select(row => row.GetProperty("track").GetProperty("id").GetInt64()).ToList();
        Assert.Contains(_ids[1], ids);
        Assert.DoesNotContain(_ids[0], ids);
        var count = await SendAsync(HttpMethod.Post, "/api/playlists/resolve-count", new { criteria, favoriteTrackIds = Array.Empty<long>() }, HttpStatusCode.OK);
        Assert.Equal(ids.Count, count.GetProperty("count").GetInt32());
    }

    /// <summary>Invalid names, malformed criteria and inappropriate regular resolution do not silently create a smart playlist.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task InvalidInput_IsRejected()
    {
        await SendAsync(HttpMethod.Post, "/api/playlists", new { name = "  ", trackIds = _ids }, HttpStatusCode.BadRequest);
        await SendAsync(HttpMethod.Post, "/api/playlists/smart", new { name = "Synthetic", filterCriteria = "malformed" }, HttpStatusCode.BadRequest);
        await SendAsync(HttpMethod.Post, "/api/playlists/resolve-count", new { criteria = "null" }, HttpStatusCode.BadRequest);
        Assert.Empty((await ReadAsync("/api/playlists")).EnumerateArray());
        var regular = await CreateAsync([]);
        await SendAsync(HttpMethod.Post, $"/api/playlists/{regular}/resolve", new { favoriteTrackIds = _ids }, HttpStatusCode.BadRequest);
    }

    /// <summary>Missing authentication and unknown profiles reject playlist mutations before database access.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task RejectedMutations_DoNotOpenDatabase()
    {
        using var anonymous = await _client.PostAsJsonAsync("/api/playlists", new { name = "Synthetic", trackIds = _ids });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await SendAsync(HttpMethod.Post, "/api/playlists", new { name = "Synthetic", trackIds = _ids }, HttpStatusCode.Forbidden, "unknown");
        Assert.Equal(0, _connections);
    }

    /// <summary>Creates a regular playlist using synthetic track IDs.</summary>
    /// <param name="ids">Selected track IDs.</param>
    /// <param name="profile">Request profile.</param>
    /// <returns>The created shared playlist ID.</returns>
    private async Task<long> CreateAsync(long[] ids, string profile = "standard")
        => (await SendAsync(HttpMethod.Post, "/api/playlists", new { name = "Synthetic", trackIds = ids }, HttpStatusCode.OK, profile)).GetProperty("id").GetInt64();

    /// <summary>Creates a smart playlist through production criteria validation.</summary>
    /// <param name="criteria">Serialized synthetic criteria.</param>
    /// <returns>The created playlist ID.</returns>
    private async Task<long> CreateSmartAsync(string criteria)
        => (await SendAsync(HttpMethod.Post, "/api/playlists/smart", new { name = "Synthetic smart", filterCriteria = criteria }, HttpStatusCode.OK)).GetProperty("id").GetInt64();

    /// <summary>Reads a production route for one synthetic profile.</summary>
    /// <param name="path">Relative route.</param>
    /// <param name="profile">Synthetic profile.</param>
    /// <returns>The response JSON.</returns>
    private Task<JsonElement> ReadAsync(string path, string profile = "standard") => SendAsync(HttpMethod.Get, path, null, HttpStatusCode.OK, profile);

    /// <summary>Sends a fixed synthetic authenticated request and checks its status.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Relative route.</param>
    /// <param name="body">Optional synthetic JSON payload.</param>
    /// <param name="status">Expected response status.</param>
    /// <param name="profile">Synthetic request profile.</param>
    /// <returns>Detached JSON for successful responses, otherwise an empty value.</returns>
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, HttpStatusCode status, string profile = "standard")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", "synthetic-playlist-key");
        request.Headers.Add("X-Orynivo-Profile", profile);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        return status == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
    }
}
