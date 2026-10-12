using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orynivo;

/// <summary>Separates temporary server profile images from positive local artist-cache identifiers.</summary>
internal static class ServerArtistProfileCacheIdentity
{
    /// <summary>Builds a stable negative cache identifier from an opaque server ID and its artist ID.</summary>
    /// <param name="serverId">Configured opaque source identifier; never a URL or credential.</param>
    /// <param name="artistId">Source-local artist identifier.</param>
    /// <returns>A stable negative identifier used only for downloaded profile image cache files.</returns>
    internal static long Create(string serverId, long artistId)
    {
        var identity = serverId.Length.ToString(CultureInfo.InvariantCulture) + ":" + serverId + ":" +
            artistId.ToString(CultureInfo.InvariantCulture);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return BinaryPrimitives.ReadInt64LittleEndian(digest) | long.MinValue;
    }
}
