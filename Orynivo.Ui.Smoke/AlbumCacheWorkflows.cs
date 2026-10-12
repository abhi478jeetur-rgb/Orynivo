using System.Diagnostics;
using System.Net;
using System.Reflection;
using Avalonia.Threading;
using Orynivo;
using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo.Ui.Smoke;

/// <summary>Checks validated disk-cache recovery through the actual remote album catalog adapter.</summary>
internal static class AlbumCacheWorkflows
{
    /// <summary>Runs failed, changed, empty, corrupt and malformed cache cases in isolated synthetic storage.</summary>
    internal static void Run()
    {
        var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        void PumpUntil(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TimeSpan.FromSeconds(10))
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Dispatcher.UIThread.RunJobs();
            Check(done(), "Album workflow timed out.");
        }
        Dispatcher.UIThread.RunJobs();
        var server = new OrynivoServerSettings { Id = "cache", Name = "Synthetic cache source", BaseUrl = "https://cache.invalid" };
        ((AppSettings)Get("_settings")!).OrynivoServers = [server];
        using var handler = new AlbumCacheHandler();
        using var transport = new HttpClient(handler);
        typeof(OrynivoServerClient).GetField("_http", flags)!.SetValue(Get("_orynivoClient"), transport);
        try
        {
            // A failed or malformed full album request cannot poison the validated disk cache.
            handler.LibraryChangedAt = 101; handler.FailAlbums = true;
            var albumContext = new LibraryCatalogLoadContext([server], ["cache:Album:21"],
                new TrackFacetFilterSnapshot(false, [], [], [], []), false, true, false, null, null);
            var albumCachePath = (string)typeof(MainWindow).GetMethod("GetOrynivoAlbumListCachePath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [server])!;
            Task<LibraryLoadResult<ContentRow>> LoadAlbums(LibraryCatalogLoadContext context) =>
                (Task<LibraryLoadResult<ContentRow>>)Call("LoadRemoteUnifiedRowsAsync", "Albums", context, CancellationToken.None)!;
            var failedAlbums = LoadAlbums(albumContext); PumpUntil(() => failedAlbums.IsCompleted);
            Check(failedAlbums.GetAwaiter().GetResult().Status == LibraryLoadStatus.Failed && !File.Exists(albumCachePath),
                "Failed album request created a valid empty disk cache");
            Console.WriteLine("PASS: failed server albums do not create a valid empty cache");
            handler.FailAlbums = false; handler.AlbumPayload = "[{\"id\":21,\"album\":\"Recovered album\",\"displayArtist\":\"Fixture artist\"}]";
            var recoveredAlbums = LoadAlbums(albumContext); PumpUntil(() => recoveredAlbums.IsCompleted);
            Check(recoveredAlbums.GetAwaiter().GetResult().Rows.Single().IsFavorite && File.Exists(albumCachePath),
                "Complete album recovery failed to populate validated disk cache");
            Console.WriteLine("PASS: recovered complete albums populate validated disk cache");
            var albumRequests = handler.AlbumRequests;
            handler.FailAlbums = true;
            var noFavoriteContext = new LibraryCatalogLoadContext([server], [],
                new TrackFacetFilterSnapshot(false, [], [], [], []), false, false, false, null, null);
            var cachedAlbums = LoadAlbums(noFavoriteContext); PumpUntil(() => cachedAlbums.IsCompleted);
            Check(cachedAlbums.GetAwaiter().GetResult().Status == LibraryLoadStatus.Complete &&
                !cachedAlbums.Result.Rows.Single().IsFavorite && handler.AlbumRequests == albumRequests,
                "Validated album cache rerequested catalog or retained previous favorites");
            Console.WriteLine("PASS: validated cached albums avoid catalog requests and reapply current favorites");
            handler.LibraryChangedAt = 102;
            var changedAlbums = LoadAlbums(noFavoriteContext); PumpUntil(() => changedAlbums.IsCompleted);
            Check(changedAlbums.GetAwaiter().GetResult().Status == LibraryLoadStatus.Failed && handler.AlbumRequests == albumRequests + 1,
                "Changed server timestamp reused stale album cache");
            Console.WriteLine("PASS: changed server timestamp rejects stale cached albums");
            handler.FailAlbums = false; handler.AlbumPayload = "[]";
            var emptyAlbums = LoadAlbums(noFavoriteContext); PumpUntil(() => emptyAlbums.IsCompleted);
            Check(emptyAlbums.GetAwaiter().GetResult().Status == LibraryLoadStatus.Complete && emptyAlbums.Result.Rows.Count == 0,
                "Successful empty album recovery was treated as failure");
            Console.WriteLine("PASS: successful empty album recovery remains complete");
            File.WriteAllText(albumCachePath, "malformed cache");
            handler.AlbumPayload = "[{\"id\":22,\"album\":\"Rebuilt album\"}]";
            var rebuiltAlbums = LoadAlbums(noFavoriteContext); PumpUntil(() => rebuiltAlbums.IsCompleted);
            Check(rebuiltAlbums.GetAwaiter().GetResult().Rows.Single().Title == "Rebuilt album", "Corrupt album disk cache prevented fresh recovery");
            Console.WriteLine("PASS: corrupt album cache rebuilds from a fresh complete response");
            var validatedAlbumBytes = File.ReadAllText(albumCachePath);
            handler.LibraryChangedAt = 103; handler.AlbumPayload = "null";
            var malformedAlbums = LoadAlbums(noFavoriteContext); PumpUntil(() => malformedAlbums.IsCompleted);
            Check(malformedAlbums.GetAwaiter().GetResult().Status == LibraryLoadStatus.Failed &&
                File.ReadAllText(albumCachePath) == validatedAlbumBytes, "Missing album payload overwrote validated cache");
            Console.WriteLine("PASS: missing album payload preserves the last validated cache");
            MainWindow.DeleteOrynivoAlbumListCache(server);
            Check(!File.Exists(albumCachePath), "Artwork invalidation retained album list cache");
            Console.WriteLine("PASS: artwork invalidation deletes the album list cache");
            handler.LibraryChangedAt = null; handler.AlbumPayload = "[]";

        }
        finally
        {
            MainWindow.DeleteOrynivoAlbumListCache(server);
            Call("ResetDrilldownState", true);
            Dispatcher.UIThread.RunJobs();
        }
    }
}

/// <summary>Serves synthetic library timestamps and controlled album catalog responses.</summary>
internal sealed class AlbumCacheHandler : HttpMessageHandler
{
    /// <summary>Gets or sets the synthetic server library timestamp.</summary>
    internal long? LibraryChangedAt { get; set; }
    /// <summary>Gets or sets whether album catalog requests fail.</summary>
    internal bool FailAlbums { get; set; }
    /// <summary>Gets or sets the full synthetic album payload, including deliberately malformed content.</summary>
    internal string AlbumPayload { get; set; } = "[]";
    /// <summary>Gets the number of full album requests.</summary>
    internal int AlbumRequests { get; private set; }
    /// <summary>Returns synthetic responses without any external HTTP transport.</summary>
    /// <param name="request">Synthetic catalog request.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>A controlled synthetic response.</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("scan", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                LibraryChangedAt is long stamp ? "{\"libraryChangedAt\":" + stamp + "}" : "{}")
            });
        if (path == "/api/albums")
        {
            AlbumRequests++;
            return Task.FromResult(new HttpResponseMessage(FailAlbums ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(FailAlbums ? "synthetic unavailable" : AlbumPayload) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
