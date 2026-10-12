using Orynivo.Library;
using Orynivo.Streaming;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks that delayed catalog work uses copied configuration, profile and personal selections.</summary>
public sealed class LibraryCatalogLoadContextTests
{
    /// <summary>Later settings/profile mutations cannot change an admitted source's connection or playback options.</summary>
    [Fact]
    public void ServerSettings_AreOperationOwnedCopies()
    {
        var server = new OrynivoServerSettings { Id = "source", Name = "Before", BaseUrl = "http://fixture.invalid",
            ApiKey = "synthetic", ProfileId = "before", StreamingFormat = "opus", StreamingBitrateKbps = 128 };
        var servers = new List<OrynivoServerSettings> { server };
        var context = Context(servers, []);
        server.ProfileId = "after"; server.BaseUrl = "http://other.invalid"; server.ApiKey = "replacement";
        server.Name = "After"; server.StreamingFormat = "aac"; server.StreamingBitrateKbps = 256;
        servers.Clear();
        var captured = Assert.Single(context.Servers);
        Assert.NotSame(server, captured);
        Assert.Equal("before", captured.ProfileId);
        Assert.Equal("http://fixture.invalid", captured.BaseUrl);
        Assert.Equal("synthetic", captured.ApiKey);
        Assert.Equal("Before", captured.Name);
        Assert.Equal("opus", captured.StreamingFormat);
        Assert.Equal(128, captured.StreamingBitrateKbps);
    }

    /// <summary>Held source completion retains original favorites and facets after their live sets change.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task DelayedSource_UsesCopiedFavoritesAndFilters()
    {
        var favorites = new HashSet<string> { "source:Track:1" };
        var genres = new HashSet<string> { "Jazz" };
        var sources = new HashSet<string> { "server:source" };
        var filters = new TrackFacetFilterSnapshot(true, genres, ["flac"], [1000], sources);
        var context = new LibraryCatalogLoadContext([], favorites, filters, true, true, true, 17, 23);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = LibrarySourceBatchLoader.LoadAsync<int, int>([1], _ => "server:source", async (_, _) =>
        {
            await gate.Task;
            return context.Favorites.Contains("source:Track:1") &&
                context.Filters.Matches(new TrackFacetInfo(1, true, "Jazz", "flac", 1000, "server:source", null))
                ? new[] { 1 } : [];
        }, default);
        favorites.Clear(); genres.Clear(); genres.Add("Rock"); sources.Clear(); sources.Add("local");
        gate.SetResult();
        Assert.Equal([1], (await pending).Rows);
        Assert.True(context.ArtistFavoritesOnly && context.AlbumFavoritesOnly && context.AlbumArtwork);
        Assert.Equal(17, context.ArtistId); Assert.Equal(23, context.AlbumId);
        Assert.False(context.Filters.IncludesSource("local"));
    }

    /// <summary>Creates a minimal operation context.</summary>
    /// <param name="servers">Synthetic server settings.</param>
    /// <param name="favorites">Synthetic favorite identities.</param>
    /// <returns>The copied operation context.</returns>
    private static LibraryCatalogLoadContext Context(IEnumerable<OrynivoServerSettings> servers,
        IEnumerable<string> favorites) => new(servers, favorites,
            new TrackFacetFilterSnapshot(false, [], [], [], []), false, false, false, null, null);
}
