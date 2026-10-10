using System.Text.Json;
using Orynivo.Library;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies remote smart-playlist references across colliding provider IDs.</summary>
public sealed class SmartPlaylistSimilarityProjectionTests
{
    /// <summary>A remote seed is projected alongside its vectors without changing stored criteria.</summary>
    /// <param name="source">Current or legacy reference provider convention.</param>
    [Theory]
    [InlineData("server:one")]
    [InlineData("ORYNIVO:ONE")]
    public void RemoteReference_ProjectsSeedAndRetainsCriteria(string source)
    {
        var criteria = new SmartPlaylistCriteria
        {
            SimilaritySourceKey = source, SimilarityTrackId = 1, FavoritesOnly = true,
            SourceKeys = ["server:two"], Genres = ["rock"], ResultLimit = 1,
            SimilarityMinimumScore = 0.9
        };
        var before = JsonSerializer.Serialize(criteria);
        var candidates = new List<SmartPlaylistTrackInfo>
        {
            Track(-1, "server:one", true), Track(-2, "server:two", true),
            Track(-3, "server:two", false), Track(1, "local", true)
        };
        var mapping = new Dictionary<(string, long), long>
        { [("server:one", 1)] = -1, [("server:two", 1)] = -2, [("server:two", 2)] = -3 };
        var result = SmartPlaylistSimilarityProjection.Resolve(criteria, candidates,
            [Vector(1, "orynivo:one"), Vector(1, "orynivo:two"), Vector(2, "orynivo:two"), Vector(1, "local")], mapping);
        Assert.Equal([-2L], result.Select(track => track.Id));
        Assert.Equal(before, JsonSerializer.Serialize(criteria));
    }

    /// <summary>A missing remote seed cannot accidentally use a colliding local ID.</summary>
    [Fact]
    public void MissingRemoteReference_DoesNotUseLocalCollision()
    {
        var criteria = new SmartPlaylistCriteria { SimilaritySourceKey = "server:gone", SimilarityTrackId = 1 };
        Assert.Empty(SmartPlaylistSimilarityProjection.Resolve(criteria,
            [Track(1, "local", true), Track(2, "local", true)], [Vector(1, "local"), Vector(2, "local")],
            new Dictionary<(string, long), long>()));
    }

    /// <summary>Local references retain their database IDs while remote neighbours use transient IDs.</summary>
    [Fact]
    public void LocalReference_IncludesRemoteNeighbour()
    {
        var criteria = new SmartPlaylistCriteria { SimilaritySourceKey = "local", SimilarityTrackId = 1 };
        var result = SmartPlaylistSimilarityProjection.Resolve(criteria,
            [Track(1, "local", true), Track(-1, "server:one", true)],
            [Vector(1, "local"), Vector(1, "orynivo:one")], new Dictionary<(string, long), long> { [("server:one", 1)] = -1 });
        Assert.Equal([-1L], result.Select(track => track.Id));
    }

    /// <summary>Ordinary criteria keep title ordering without requiring feature vectors.</summary>
    [Fact]
    public void WithoutReference_UsesOrdinaryCriteria()
    {
        var result = SmartPlaylistSimilarityProjection.Resolve(new() { SourceKeys = ["local"] },
            [Track(-1, "server:one", true), Track(1, "local", true)], null, new Dictionary<(string, long), long>());
        Assert.Equal([1L], result.Select(track => track.Id));
    }

    private static SmartPlaylistTrackInfo Track(long id, string source, bool favorite) =>
        new(id, favorite, "rock", "flac", 1000, 2000, "Artist", "Album", 200, 1, 0, null, "Fixture", source);

    private static SimilarityFeatureVector Vector(long id, string source) =>
        new(SimilarityFeatureService.CurrentVersion, source, id, id, "artist", ["rock"], [], 0.5, 0.5, 0, 0, null);
}
