using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orynivo.Library;

/// <summary>A single image result from a Wikimedia Commons search.</summary>
/// <param name="Title">Cleaned file title (without "File:" prefix or extension).</param>
/// <param name="Attribution">Attribution text from the image metadata, or <see langword="null"/>.</param>
/// <param name="License">Short licence identifier, e.g. <c>CC BY-SA 4.0</c>, or <see langword="null"/>.</param>
/// <param name="SourceUrl">Wikimedia Commons description page URL.</param>
/// <param name="ImageData">Raw image bytes of the 600 px thumbnail.</param>
/// <param name="MimeType">MIME type of <paramref name="ImageData"/>.</param>
public sealed record ArtistImageSearchResult(
    string Title,
    string? Attribution,
    string? License,
    string SourceUrl,
    byte[] ImageData,
    string? MimeType);

/// <summary>
/// Searches Wikimedia Commons for artist images and downloads 600 px thumbnails.
/// </summary>
public static class ArtistImageSearchService
{
    private static readonly HttpClient Client = CreateClient();

    /// <summary>Searches Wikimedia Commons for images matching <paramref name="query"/> and returns up to 12 results.</summary>
    /// <param name="query">Free-text search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<List<ArtistImageSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
        => await SearchAsync(query, 12, cancellationToken);

    /// <summary>Finds the first Wikimedia Commons image candidate without persisting it.</summary>
    /// <param name="artistName">Artist name used for the Wikimedia Commons search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first candidate, or <see langword="null"/> when no image was found.</returns>
    public static async Task<ArtistImageSearchResult?> FindBestAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return null;

        var results = await SearchAsync($"\"{artistName.Trim()}\" musician", 1, cancellationToken);
        return results.FirstOrDefault();
    }

    private static async Task<List<ArtistImageSearchResult>> SearchAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var encodedQuery = Uri.EscapeDataString(query.Trim());
        var resultLimit = Math.Clamp(maximumResults, 1, 12);
        var uri =
            "https://commons.wikimedia.org/w/api.php" +
            $"?action=query&generator=search&gsrsearch={encodedQuery}" +
            $"&gsrnamespace=6&gsrlimit={resultLimit}&prop=imageinfo" +
            "&iiprop=url%7Cmime%7Cextmetadata&iiurlwidth=600" +
            "&format=json&formatversion=2&origin=*";

        using var response = await Client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
            !queryElement.TryGetProperty("pages", out var pagesElement))
        {
            return [];
        }

        var results = new List<ArtistImageSearchResult>();
        foreach (var page in pagesElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!page.TryGetProperty("imageinfo", out var imageInfoArray))
                continue;

            var imageInfo = imageInfoArray.EnumerateArray().FirstOrDefault();
            if (imageInfo.ValueKind == JsonValueKind.Undefined ||
                !imageInfo.TryGetProperty("thumburl", out var thumbnailUrlElement))
            {
                continue;
            }

            var thumbnailUrl = thumbnailUrlElement.GetString();
            var sourceUrl = imageInfo.TryGetProperty("descriptionurl", out var sourceUrlElement)
                ? sourceUrlElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(thumbnailUrl) || string.IsNullOrWhiteSpace(sourceUrl))
                continue;

            using var imageResponse = await Client.GetAsync(thumbnailUrl, cancellationToken);
            if (!imageResponse.IsSuccessStatusCode)
                continue;

            var imageData = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (imageData.Length == 0)
                continue;

            var metadata = imageInfo.TryGetProperty("extmetadata", out var metadataElement)
                ? metadataElement
                : default;
            results.Add(new ArtistImageSearchResult(
                CleanTitle(page.TryGetProperty("title", out var title) ? title.GetString() : null),
                ReadMetadata(metadata, "Artist"),
                ReadMetadata(metadata, "LicenseShortName"),
                sourceUrl,
                imageData,
                imageResponse.Content.Headers.ContentType?.MediaType));
        }

        return results;
    }

    /// <summary>
    /// Writes the image from <paramref name="result"/> to the artist-images directory, replacing any existing
    /// image for <paramref name="artistId"/>, and returns the saved file path.
    /// </summary>
    /// <param name="artistId">Database artist ID used as the file name stem.</param>
    /// <param name="result">Image to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Absolute path of the saved image file.</returns>
    public static async Task<string> SaveAsync(
        long artistId,
        ArtistImageSearchResult result,
        CancellationToken cancellationToken = default)
        => await SaveImageAsync(artistId, result.ImageData, result.MimeType, cancellationToken);

    /// <summary>
    /// Exclusively replaces cached image variants for the artist through a unique staging file.
    /// Cancellation before publication preserves the previous image; failed staging is cleaned up.
    /// </summary>
    /// <param name="artistId">Database artist ID used as the file name stem.</param>
    /// <param name="imageData">Raw image bytes to store.</param>
    /// <param name="mimeType">MIME type used to select the file extension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Absolute path of the saved image file.</returns>
    public static async Task<string> SaveImageAsync(
        long artistId,
        byte[] imageData,
        string? mimeType,
        CancellationToken cancellationToken = default)
    {
        if (imageData.Length == 0)
            throw new ArgumentException("Image data is required.", nameof(imageData));

        var extension = mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
        var directory = Path.GetFullPath(AppPaths.GetDataPath("artist-images"));
        using var mutation = await ArtistImageMutationGate.EnterAsync(
            Path.Combine(directory, artistId.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, artistId + extension);
        var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, imageData, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, true);
            foreach (var existingPath in Directory.EnumerateFiles(directory, artistId + ".*"))
            {
                if (!string.Equals(existingPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    File.Delete(existingPath);
            }
            return targetPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>Deletes cached artist variants through the shared mutation gate; use asynchronously on UI callers.</summary>
    /// <param name="artistId">Database artist ID used as the cached file-name stem.</param>
    public static void DeleteImage(long artistId)
        => DeleteImageAsync(artistId).GetAwaiter().GetResult();

    /// <summary>Deletes cached image variants after exclusive same-artist mutation admission.</summary>
    /// <param name="artistId">Database artist ID used as the cached file-name stem.</param>
    /// <param name="cancellationToken">Cancellation before deletion admission.</param>
    /// <returns>A task completing serialized variant deletion.</returns>
    public static async Task DeleteImageAsync(long artistId, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(AppPaths.GetDataPath("artist-images"));
        using var mutation = await ArtistImageMutationGate.EnterAsync(
            Path.Combine(directory, artistId.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, artistId + ".*")) File.Delete(path);
    }

    private static string CleanTitle(string? title)
    {
        var value = title ?? string.Empty;
        if (value.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
            value = value[5..];
        return Path.GetFileNameWithoutExtension(value.Replace('_', ' '));
    }

    private static string? ReadMetadata(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty(propertyName, out var property) ||
            !property.TryGetProperty("value", out var value))
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Orynivo/1.0 (Windows music player; artist image search)");
        return client;
    }
}
