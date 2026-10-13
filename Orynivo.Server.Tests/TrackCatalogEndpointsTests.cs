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

/// <summary>Exercises production track routes with real middleware and isolated temporary SQLite.</summary>
public sealed class TrackCatalogEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orynivo-track-api-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _trackId;
    private int _connections;
    private string DatabasePath => Path.Combine(_root, "library.db");

    /// <summary>Seeds synthetic tracks and starts only the production route/middleware subset in memory.</summary>
    /// <returns>The host startup task.</returns>
    public async Task InitializeAsync()
    {
        using (var db = new AudioDatabase(DatabasePath))
        {
            for (var index = 0; index < 9; index++)
                db.Upsert(new TrackRecord
                {
                    Path = Path.Combine(_root, $"track-{index}.flac"),
                    FileName = $"track-{index}.flac",
                    Title = $"Track {index:D2}",
                    Artist = "Synthetic artist",
                    Album = "Synthetic album",
                    Format = "flac"
                });
            _trackId = db.GetTrackListPage(0, 1).Single().Id;
            var previousProfile = AudioDatabase.ActiveProfileId;
            try { AudioDatabase.SetActiveProfile("alpha"); db.SetTrackUserRating(_trackId, 4); }
            finally { AudioDatabase.SetActiveProfile(previousProfile); }
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ServerSettings
        {
            ApiKey = "synthetic-track-key",
            Profiles = [new ServerProfile(), new ServerProfile { Id = "alpha", Name = "Alpha" },
                new ServerProfile { Id = "beta", Name = "Beta" }]
        });
        _app = builder.Build();
        _app.UseMiddleware<ApiKeyMiddleware>();
        _app.UseMiddleware<ProfileContextMiddleware>();
        _app.MapTrackCatalogEndpoints(() => { Interlocked.Increment(ref _connections); return new AudioDatabase(DatabasePath); });
        _app.MapAlbumBatchEndpoints(() => new AudioDatabase(DatabasePath));
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

    /// <summary>Pagination returns stable non-overlapping pages and a successful empty final page.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task Pages_AreOrderedCompleteAndDisjoint()
    {
        var titles = new List<string>();
        for (var page = 0; page < 4; page++)
        {
            var rows = await ReadAsync($"/api/tracks?page={page}&pageSize=3");
            Assert.Equal(page < 3 ? 3 : 0, rows.GetArrayLength());
            titles.AddRange(rows.EnumerateArray().Select(row => row.GetProperty("title").GetString()!));
        }
        Assert.Equal(Enumerable.Range(0, 9).Select(index => $"Track {index:D2}"), titles);
    }

    /// <summary>Negative pages and bounded page sizes preserve the existing HTTP contract.</summary>
    /// <param name="page">Requested page.</param>
    /// <param name="size">Requested page size.</param>
    /// <param name="count">Expected result count after normalization.</param>
    /// <returns>The endpoint assertion task.</returns>
    [Theory]
    [InlineData(-1, 3, 3)]
    [InlineData(0, 0, 1)]
    [InlineData(0, -10, 1)]
    [InlineData(0, 6000, 9)]
    public async Task Pages_NormalizeBounds(int page, int size, int count)
        => Assert.Equal(count, (await ReadAsync($"/api/tracks?page={page}&pageSize={size}")).GetArrayLength());

    /// <summary>Single-track and batch resolution return existing rows and reject or omit absent IDs.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task DetailAndBatch_ResolveOnlyExistingTracks()
    {
        var detail = await ReadAsync($"/api/tracks/{_trackId}");
        Assert.Equal("Track 00", detail.GetProperty("title").GetString());
        using var missing = Request(HttpMethod.Get, "/api/tracks/9223372036854775807");
        using var missingResponse = await _client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        using var batch = Request(HttpMethod.Post, "/api/tracks/by-ids");
        batch.Content = JsonContent.Create(new[] { _trackId, long.MaxValue });
        using var response = await _client.SendAsync(batch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_trackId, Assert.Single(rows.EnumerateArray()).GetProperty("id").GetInt64());
    }

    /// <summary>Authentication and unknown-profile rejection occur before opening the fixture database.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task RejectedRequests_DoNotOpenDatabase()
    {
        using var anonymous = await _client.GetAsync("/api/tracks");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var unknown = Request(HttpMethod.Put, $"/api/tracks/{_trackId}/favorite", "unknown");
        unknown.Content = JsonContent.Create(new { isFavorite = true });
        using var response = await _client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _connections);
    }

    /// <summary>Personal favorite writes remain scoped during overlapping profile reads, including the standard default.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task Favorites_AreIsolatedAcrossProfiles()
    {
        using var write = Request(HttpMethod.Put, $"/api/tracks/{_trackId}/favorite", "alpha");
        write.Content = JsonContent.Create(new { isFavorite = true });
        using var response = await _client.SendAsync(write);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reads = await Task.WhenAll(ReadAsync("/api/tracks", "alpha"), ReadAsync("/api/tracks", "beta"), ReadAsync("/api/tracks"));
        Assert.True(reads[0][0].GetProperty("isFavorite").GetBoolean());
        Assert.False(reads[1][0].GetProperty("isFavorite").GetBoolean());
        Assert.False(reads[2][0].GetProperty("isFavorite").GetBoolean());
        Assert.Equal(4, reads[0][0].GetProperty("userRating").GetInt32());
        Assert.Equal(0, reads[1][0].GetProperty("userRating").GetInt32());
        Assert.Equal(0, reads[2][0].GetProperty("userRating").GetInt32());
        foreach (var profile in new[] { "alpha", "beta" })
        {
            using var batch = Request(HttpMethod.Post, "/api/tracks/by-ids", profile);
            batch.Content = JsonContent.Create(new[] { _trackId });
            using var batchResponse = await _client.SendAsync(batch);
            Assert.Equal(HttpStatusCode.OK, batchResponse.StatusCode);
            var rows = await batchResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(profile == "alpha", rows[0].GetProperty("isFavorite").GetBoolean());
            Assert.Equal(profile == "alpha" ? 4 : 0, rows[0].GetProperty("userRating").GetInt32());
        }
        using var missing = Request(HttpMethod.Put, "/api/tracks/9223372036854775807/favorite", "alpha");
        missing.Content = JsonContent.Create(new { isFavorite = true });
        using var missingResponse = await _client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    /// <summary>Selected album resolution preserves profiles and rejects oversized or unauthenticated batches.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task AlbumBatch_IsBoundedSelectedAndProfileAware()
    {
        long albumId;
        using (var db = new AudioDatabase(DatabasePath))
        {
            albumId = Assert.Single(db.GetAlbumsLite()).Id;
            var previous = AudioDatabase.ActiveProfileId;
            try { AudioDatabase.SetActiveProfile("alpha"); db.SetAlbumFavorite(albumId, true); }
            finally { AudioDatabase.SetActiveProfile(previous); }
        }
        foreach (var profile in new[] { "alpha", "beta" })
        {
            using var request = Request(HttpMethod.Post, "/api/albums/by-ids", profile);
            request.Content = JsonContent.Create(new[] { albumId, albumId, long.MaxValue });
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, rows.GetArrayLength());
            Assert.Equal(albumId, rows[0].GetProperty("id").GetInt64());
            Assert.Equal(profile == "alpha", rows[0].GetProperty("isFavorite").GetBoolean());
            Assert.True(rows[0].GetProperty("artistId").GetInt64() > 0);
        }
        using var oversized = Request(HttpMethod.Post, "/api/albums/by-ids");
        oversized.Content = JsonContent.Create(Enumerable.Range(0, 101).Select(i => (long)i));
        using var rejected = await _client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var empty = Request(HttpMethod.Post, "/api/albums/by-ids");
        empty.Content = JsonContent.Create(Array.Empty<long>());
        using var emptyResponse = await _client.SendAsync(empty);
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        Assert.Empty((await emptyResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, "/api/albums/by-ids");
        anonymous.Content = JsonContent.Create(new[] { albumId });
        using var forbidden = await _client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
    }

    /// <summary>Reads production JSON while requiring a successful authenticated response.</summary>
    /// <param name="path">Synthetic relative route.</param>
    /// <param name="profile">Optional synthetic profile header.</param>
    /// <returns>The detached response JSON.</returns>
    private async Task<JsonElement> ReadAsync(string path, string? profile = null)
    {
        using var request = Request(HttpMethod.Get, path, profile);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Creates a request with fixed synthetic authentication only.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Relative route.</param>
    /// <param name="profile">Optional synthetic profile.</param>
    /// <returns>The caller-owned request.</returns>
    private static HttpRequestMessage Request(HttpMethod method, string path, string? profile = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", "synthetic-track-key");
        if (profile is not null) request.Headers.Add("X-Orynivo-Profile", profile);
        return request;
    }
}
