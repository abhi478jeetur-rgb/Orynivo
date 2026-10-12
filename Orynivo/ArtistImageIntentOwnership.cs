using Orynivo.Library;

namespace Orynivo;

/// <summary>Retains dispatcher-owned image supersession only for currently observed artist operations.</summary>
internal sealed class ArtistImageIntentOwnership
{
    private readonly HashSet<Observation> _observations = [];

    /// <summary>Gets the number of live observations, without retaining completed artist history.</summary>
    internal int ActiveObservationCount => _observations.Count;

    /// <summary>Observes fixed source-local and normalized artist identities before asynchronous work.</summary>
    /// <param name="targets">Opaque source IDs, artist IDs and canonical names; no connection credentials.</param>
    /// <returns>A disposable observation released when its operation finishes.</returns>
    internal Observation Observe(params (string? ServerId, long? ArtistId, string? Name)[] targets)
    {
        var observation = new Observation(this, targets.Select(target => new Identity(
            target.ServerId, target.ArtistId, ArtistNameNormalizer.CreateComparisonKey(target.Name))).ToArray());
        _observations.Add(observation);
        return observation;
    }

    /// <summary>Permanently supersedes matching live observations, preserving rejection across unrelated later writes.</summary>
    /// <param name="serverId">Opaque owning server ID, or null for local artists.</param>
    /// <param name="artistId">Source-local artist ID, when available.</param>
    /// <param name="artistName">Canonical artist name for cross-source matching.</param>
    internal void Admit(string? serverId, long? artistId, string? artistName)
    {
        var identity = new Identity(serverId, artistId, ArtistNameNormalizer.CreateComparisonKey(artistName));
        foreach (var observation in _observations) observation.Supersede(identity);
    }

    /// <summary>Stores credential-free identity retained only during active work.</summary>
    /// <param name="ServerId">Opaque source ID.</param>
    /// <param name="ArtistId">Source-local artist ID.</param>
    /// <param name="NameKey">Normalized canonical artist name.</param>
    internal readonly record struct Identity(string? ServerId, long? ArtistId, string NameKey)
    {
        /// <summary>Matches source-local IDs or a non-empty normalized cross-source name.</summary>
        /// <param name="other">Identity being compared.</param>
        /// <returns>Whether both identify the same artist.</returns>
        internal bool Matches(Identity other) =>
            (ArtistId.HasValue && other.ArtistId.HasValue && ArtistId == other.ArtistId && ServerId == other.ServerId) ||
            (NameKey.Length > 0 && NameKey == other.NameKey);
    }

    /// <summary>Owns a fixed live identity set and latched supersession flags for each identity.</summary>
    internal sealed class Observation : IDisposable
    {
        private readonly ArtistImageIntentOwnership _owner;
        private readonly Identity[] _targets;
        private readonly bool[] _superseded;
        private bool _disposed;

        /// <summary>Creates an observation registered by its owner.</summary>
        /// <param name="owner">Dispatcher-affine registry.</param>
        /// <param name="targets">Fixed observed artist identities.</param>
        internal Observation(ArtistImageIntentOwnership owner, Identity[] targets)
        {
            _owner = owner; _targets = targets; _superseded = new bool[targets.Length];
        }

        /// <summary>Checks retained rejection for a matching artist, or any use after disposal.</summary>
        /// <param name="serverId">Opaque source ID.</param>
        /// <param name="artistId">Source-local artist ID.</param>
        /// <param name="artistName">Optional canonical name.</param>
        /// <returns>Whether the requested image must no longer publish.</returns>
        internal bool WasSupersededFor(string? serverId, long? artistId, string? artistName)
        {
            if (_disposed) return true;
            var identity = new Identity(serverId, artistId, ArtistNameNormalizer.CreateComparisonKey(artistName));
            for (var index = 0; index < _targets.Length; index++)
                if (_superseded[index] && _targets[index].Matches(identity)) return true;
            return false;
        }

        /// <summary>Latches matching admission without keeping a mutation history.</summary>
        /// <param name="identity">New image mutation identity.</param>
        internal void Supersede(Identity identity)
        {
            for (var index = 0; index < _targets.Length; index++)
                if (_targets[index].Matches(identity)) _superseded[index] = true;
        }

        /// <summary>Binds resolved canonical metadata to an already observed source-local ID without reviving rejection.</summary>
        /// <param name="serverId">Original opaque source ID.</param>
        /// <param name="artistId">Already observed artist ID.</param>
        /// <param name="artistName">Resolved canonical name.</param>
        internal void BindCanonicalName(string? serverId, long artistId, string? artistName)
        {
            if (_disposed) return;
            for (var index = 0; index < _targets.Length; index++)
                if (_targets[index].ServerId == serverId && _targets[index].ArtistId == artistId)
                    _targets[index] = _targets[index] with { NameKey = ArtistNameNormalizer.CreateComparisonKey(artistName) };
        }

        /// <summary>Renews this operation after its own admitted manual-donor propagation.</summary>
        internal void Renew()
        {
            if (!_disposed) Array.Clear(_superseded);
        }

        /// <summary>Releases all observed identities; repeated disposal is harmless.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._observations.Remove(this);
        }
    }
}
