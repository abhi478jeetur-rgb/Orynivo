using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orynivo;

/// <summary>Stores bounded, credential-free view previews with atomic off-thread I/O.</summary>
internal sealed class ViewPreviewStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private readonly string _directory;

    /// <summary>Creates storage beneath the application's data root.</summary>
    /// <param name="dataRoot">Application-owned data directory.</param>
    internal ViewPreviewStore(string dataRoot) => _directory = Path.Combine(dataRoot, "view-previews-v1");

    /// <summary>Compares transient display data without storing its raw values.</summary>
    /// <param name="value">Transient display or configuration inputs.</param>
    /// <returns>A deterministic content digest.</returns>
    internal static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,
            new JsonSerializerOptions { IncludeFields = true })));

    /// <summary>Reads a recent preview without blocking the dispatcher.</summary>
    /// <typeparam name="T">Credential-free preview schema.</typeparam>
    /// <param name="identity">Opaque profile/source context.</param>
    /// <param name="section">Stable view/options identity.</param>
    /// <returns>The preview, or null for missing, old or corrupt files.</returns>
    internal Task<T?> ReadAsync<T>(string identity, string section) where T : class => Task.Run(async () =>
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = PathFor(identity, section);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4 * 1024 * 1024 || info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                return null;
            return JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path).ConfigureAwait(false), Options);
        }
        catch (Exception) { return null; }
        finally { Gate.Release(); }
    });

    /// <summary>Atomically saves admitted display data and evicts old previews.</summary>
    /// <typeparam name="T">Credential-free preview schema.</typeparam>
    /// <param name="identity">Opaque profile/source context.</param>
    /// <param name="section">Stable view/options identity.</param>
    /// <param name="preview">Sanitized display data.</param>
    /// <param name="canWrite">Thread-safe configuration ownership guard.</param>
    /// <returns>A task completing after best-effort persistence.</returns>
    internal Task WriteAsync<T>(string identity, string section, T preview, Func<bool> canWrite) => Task.Run(async () =>
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        string? staging = null;
        try
        {
            if (!canWrite()) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(preview, Options);
            if (bytes.Length > 4 * 1024 * 1024) return;
            Directory.CreateDirectory(_directory);
            var path = PathFor(identity, section);
            staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(staging, bytes).ConfigureAwait(false);
            if (!canWrite()) return;
            File.Move(staging, path, overwrite: true);
            foreach (var old in new DirectoryInfo(_directory).EnumerateFiles("*.json")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(64))
                old.Delete();
        }
        catch (Exception) { /* Optional previews never prevent navigation. */ }
        finally
        {
            if (staging is not null)
                try { File.Delete(staging); } catch (Exception) { }
            Gate.Release();
        }
    });

    /// <summary>Removes previews when configuration or authentication changes.</summary>
    /// <returns>A task completing after best-effort removal.</returns>
    internal Task ClearAsync() => Task.Run(async () =>
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_directory))
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json")) File.Delete(file);
        }
        catch (Exception) { }
        finally { Gate.Release(); }
    });

    /// <summary>Derives a filename without retaining context values in directory listings.</summary>
    /// <param name="identity">Opaque profile/source context.</param>
    /// <param name="section">Stable view/options identity.</param>
    /// <returns>The application-owned preview path.</returns>
    private string PathFor(string identity, string section) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + ";" + section))) + ".json");
}
