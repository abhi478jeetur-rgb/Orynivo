using System.Diagnostics.CodeAnalysis;

namespace Orynivo;

/// <summary>Identifies a server navigation destination without retaining connection settings or credentials.</summary>
/// <param name="ServerId">Owning configured server identity.</param>
/// <param name="View">Existing server view identity, including legacy Root destinations.</param>
internal sealed record ServerNavigationTarget(string ServerId, string View)
{
    /// <summary>Gets the existing sidebar tag for this source-bound destination.</summary>
    internal string Tag => $"OrynivoServer:{ServerId}:{View}";

    /// <summary>Parses the existing server tag grammar without resolving a provider or changing UI state.</summary>
    /// <param name="tag">Potential server sidebar tag.</param>
    /// <param name="target">Source-bound destination when parsing succeeds.</param>
    /// <returns>Whether the tag names a nonblank server identity.</returns>
    internal static bool TryParse(string? tag, [NotNullWhen(true)] out ServerNavigationTarget? target)
    {
        target = null;
        if (tag?.StartsWith("OrynivoServer:", StringComparison.Ordinal) != true) return false;
        var parts = tag.Split(':', 3);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return false;
        target = new(parts[1], parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "Root");
        return true;
    }

    /// <summary>Retains an album's origin only when it belongs to the album's owning server.</summary>
    /// <param name="serverId">Active catalog album's server identity.</param>
    /// <param name="originTag">Current sidebar tag, which may belong to another source.</param>
    /// <returns>The exact original matching tag or the owning server's Albums tag.</returns>
    internal static string ForAlbum(string serverId, string? originTag) =>
        TryParse(originTag, out var origin) && string.Equals(origin.ServerId, serverId, StringComparison.Ordinal)
            ? originTag!
            : new ServerNavigationTarget(serverId, "Albums").Tag;
}
