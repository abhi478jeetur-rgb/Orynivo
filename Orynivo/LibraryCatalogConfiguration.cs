using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Compares transient server configuration without persisting or logging connection details.</summary>
internal static class LibraryCatalogConfiguration
{
    /// <summary>Determines whether ordered sources retain all catalog and playback connection settings.</summary>
    /// <param name="left">Previous configured sources; null is an empty selection.</param>
    /// <param name="right">New configured sources; null is an empty selection.</param>
    /// <returns>Whether no source setting requiring catalog invalidation changed.</returns>
    internal static bool MatchesServers(IReadOnlyList<OrynivoServerSettings>? left,
        IReadOnlyList<OrynivoServerSettings>? right)
    {
        left ??= [];
        right ??= [];
        return left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal) &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.BaseUrl, pair.Second.BaseUrl, StringComparison.Ordinal) &&
            string.Equals(pair.First.ApiKey, pair.Second.ApiKey, StringComparison.Ordinal) &&
            string.Equals(pair.First.ProfileId, pair.Second.ProfileId, StringComparison.Ordinal) &&
            string.Equals(pair.First.StreamingFormat, pair.Second.StreamingFormat, StringComparison.Ordinal) &&
            pair.First.StreamingBitrateKbps == pair.Second.StreamingBitrateKbps);
    }
}
