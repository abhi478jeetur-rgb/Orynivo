using System.Text.Json;
using Orynivo.Library;

namespace Orynivo;

/// <summary>Projects provider-local similarity identities onto transient unified playlist IDs.</summary>
internal static class SmartPlaylistSimilarityProjection
{
    /// <summary>Resolves criteria after translating both remote vectors and the reference identity.</summary>
    /// <param name="criteria">Persisted criteria, which remain unchanged.</param>
    /// <param name="candidates">Unified candidates with negative IDs for remote tracks.</param>
    /// <param name="features">Original provider-local feature vectors, or null when unavailable.</param>
    /// <param name="remoteIds">Mapping from provider key and real track ID to transient candidate ID.</param>
    /// <returns>Matching candidates with their transient IDs and configured filters intact.</returns>
    internal static List<SmartPlaylistTrackInfo> Resolve(
        SmartPlaylistCriteria criteria,
        List<SmartPlaylistTrackInfo> candidates,
        IReadOnlyList<SimilarityFeatureVector>? features,
        IReadOnlyDictionary<(string Source, long Id), long> remoteIds)
    {
        if (string.IsNullOrWhiteSpace(criteria.SimilaritySourceKey) || criteria.SimilarityTrackId is null)
            return criteria.Resolve(candidates);
        if (features is not { Count: > 0 })
            return [];
        var projectedIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in remoteIds)
            projectedIds[$"{entry.Key.Source}\u001f{entry.Key.Id}"] = entry.Value;

        var source = NormalizeSource(criteria.SimilaritySourceKey);
        var seedId = criteria.SimilarityTrackId.Value;
        if (source.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
        {
            if (!projectedIds.TryGetValue($"{source}\u001f{seedId}", out seedId))
                return [];
        }

        // Clone every persisted field rather than maintaining a second list of criteria properties.
        var json = JsonSerializer.SerializeToNode(criteria)!;
        json[nameof(SmartPlaylistCriteria.SimilaritySourceKey)] = source;
        json[nameof(SmartPlaylistCriteria.SimilarityTrackId)] = seedId;
        var runtimeCriteria = json.Deserialize<SmartPlaylistCriteria>()!;
        var projected = new List<SimilarityFeatureVector>(features.Count);
        foreach (var feature in features)
        {
            var featureSource = NormalizeSource(feature.SourceKey);
            if (featureSource.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                if (projectedIds.TryGetValue($"{featureSource}\u001f{feature.TrackId}", out var id))
                    projected.Add(feature with { SourceKey = featureSource, TrackId = id });
            }
            else
                projected.Add(feature);
        }
        return runtimeCriteria.Resolve(candidates, projected);
    }

    /// <summary>Converts the similarity provider convention to the playlist provider convention.</summary>
    /// <param name="source">Credential-free provider key.</param>
    /// <returns>The corresponding playlist provider key.</returns>
    private static string NormalizeSource(string source) =>
        source.StartsWith("orynivo:", StringComparison.OrdinalIgnoreCase)
            ? "server:" + source["orynivo:".Length..]
            : source;
}
