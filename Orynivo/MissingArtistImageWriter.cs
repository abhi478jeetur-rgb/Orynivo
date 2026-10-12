using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>Stores accepted missing-image candidates with current authoritative state and ordered artist writes.</summary>
internal sealed class MissingArtistImageWriter
{
    private readonly ArtistImageWriteQueue _queue;
    private readonly Action<string?, long?, string>? _admitImage;

    /// <summary>Creates a writer sharing the owning window's artist admission queue.</summary>
    /// <param name="queue">Shared admission, or standalone Settings admission.</param>
    /// <param name="admitImage">Optional window image-intent notification before an eligible mutation.</param>
    internal MissingArtistImageWriter(ArtistImageWriteQueue queue,
        Action<string?, long?, string>? admitImage = null)
    {
        _queue = queue;
        _admitImage = admitImage;
    }

    /// <summary>Stores a candidate only when its original artist still lacks artwork and has no manual image marker.</summary>
    /// <param name="artistId">Source-local artist identifier.</param>
    /// <param name="artistName">Original name used for admission and identity validation.</param>
    /// <param name="server">Copied owning server, or null for the local library.</param>
    /// <param name="imageData">Accepted candidate bytes.</param>
    /// <param name="mimeType">Candidate MIME type.</param>
    /// <param name="client">Caller-owned remote transport.</param>
    /// <param name="cancellationToken">Cancellation for the Settings run.</param>
    /// <param name="canWrite">Original run and source ownership, evaluated on the caller context.</param>
    /// <param name="changed">Optional catalog invalidation after an admitted mutation.</param>
    /// <returns>True for storage, false for upload failure, or null for skipped obsolete or populated targets.</returns>
    internal async Task<bool?> StoreAsync(long artistId, string artistName,
        OrynivoServerSettings? server, byte[] imageData, string? mimeType,
        OrynivoServerClient client, CancellationToken cancellationToken,
        Func<bool> canWrite, Action? changed = null)
    {
        var key = ArtistNameNormalizer.CreateComparisonKey(artistName);
        if (key.Length == 0) return null;
        bool IsCurrent() => !cancellationToken.IsCancellationRequested && canWrite();
        bool? result = null;
        await _queue.RunAsync(key, IsCurrent, async () =>
        {
            var mutated = false;
            try
            {
                if (server is null)
                {
                    var missing = await Task.Run(() =>
                    {
                        using var db = AudioDatabase.OpenDefault();
                        cancellationToken.ThrowIfCancellationRequested();
                        var artist = db.GetArtistById(artistId);
                        return artist is not null && !artist.ImageIsManual &&
                            ArtistNameNormalizer.CreateComparisonKey(artist.Artist) == key &&
                            (string.IsNullOrWhiteSpace(artist.ImagePath) || !File.Exists(artist.ImagePath));
                    }, cancellationToken);
                    if (!IsCurrent() || !missing) return false;
                    _admitImage?.Invoke(null, artistId, artistName);
                    var path = await ArtistImageSearchService.SaveImageAsync(
                        artistId, imageData, mimeType, cancellationToken);
                    mutated = true;
                    await Task.Run(() =>
                    {
                        using var db = AudioDatabase.OpenDefault();
                        cancellationToken.ThrowIfCancellationRequested();
                        db.UpdateArtistImage(artistId, path);
                    }, cancellationToken);
                    result = true;
                }
                else
                {
                    var artist = await client.GetArtistAsync(server, artistId, cancellationToken);
                    if (!IsCurrent() || artist is null || artist.ImageIsManual || artist.HasImage ||
                        ArtistNameNormalizer.CreateComparisonKey(artist.Name) != key) return false;
                    _admitImage?.Invoke(server.Id, artistId, artistName);
                    result = await client.UploadArtistImageAsync(server, artistId,
                        imageData, mimeType, cancellationToken);
                    mutated = result == true;
                }
                return result == true;
            }
            finally
            {
                if (mutated) changed?.Invoke();
            }
        });
        return result;
    }
}
