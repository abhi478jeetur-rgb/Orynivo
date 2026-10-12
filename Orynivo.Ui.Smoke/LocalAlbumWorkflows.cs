using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orynivo;
using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo.Ui.Smoke;

/// <summary>Checks local album publication and a populated search/album/Back journey.</summary>
internal static class LocalAlbumWorkflows
{
    /// <summary>Runs production adapters with synthetic database and HTTP data.</summary>
    /// <param name="root">Process-owned temporary directory.</param>
    internal static void Run(string root)
    {
        long localAlbum;
        using (var db = AudioDatabase.OpenDefault())
        {
            db.Upsert(new TrackRecord
            {
                Path = Path.Combine(root, "journey.flac"),
                FileName = "journey.flac",
                Title = "Journey local track",
                Artist = "Journey artist",
                AlbumArtist = "Journey artist",
                Album = "Journey album",
                Format = "flac"
            });
            localAlbum = db.GetAlbumsLite().Single(album => album.Album == "Journey album").Id;
        }
        var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
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
        var grid = window.FindControl<DataGrid>("ContentDataGrid")!;
        try
        {
            // Local workers complete off-thread; hold their dispatcher publication while changing navigation.
            Task OpenLocalAlbum(IReadOnlyList<long>? ids = null) => (Task)Call("ShowAlbumTracksAsync", localAlbum, "Local fixture", null, null, ids)!;
            foreach (var abandonLocal in new[] { "reset", "top-level", "configuration" })
            {
                Call("ResetDrilldownState", true); Set("_currentTopLevelTag", "Albums");
                var localOpen = OpenLocalAlbum();
                Check(!localOpen.IsCompleted, "Local worker completed before held publication check");
                var localToken = ((CancellationTokenSource)Get("_localAlbumCts")!).Token;
                if (abandonLocal == "reset") Call("ResetDrilldownState", true);
                else if (abandonLocal == "configuration") Call("InvalidateCatalogConfiguration");
                else { var queueView = (Task)Call("ShowTopLevelViewAsync", "Queue")!; PumpUntil(() => queueView.IsCompleted); queueView.GetAwaiter().GetResult(); }
                var localSentinel = new[] { new ContentRow { Title = "New local view" } };
                var headerSentinel = new object();
                grid.ItemsSource = localSentinel; grid.Opacity = 0.66;
                window.FindControl<Control>("AlbumDetailHeader")!.DataContext = headerSentinel;
                window.FindControl<Button>("BackButton")!.IsVisible = false;
                Check(localToken.IsCancellationRequested && (int)Get("_contentLoadingDepth")! == 0, "Local cancellation leaked loading");
                PumpUntil(() => localOpen.IsCompleted); localOpen.GetAwaiter().GetResult();
                Check(ReferenceEquals(grid.ItemsSource, localSentinel) && grid.Opacity == 0.66 &&
                    ReferenceEquals(window.FindControl<Control>("AlbumDetailHeader")!.DataContext, headerSentinel) &&
                    !window.FindControl<Button>("BackButton")!.IsVisible && (int)Get("_contentLoadingDepth")! == 0,
                    "Obsolete local worker changed rows/header/fade/Back or loading depth");
                Console.WriteLine("PASS: local album rejects held " + abandonLocal + " publication including header/fade/Back");
            }
            Call("ResetDrilldownState", true); Set("_currentTopLevelTag", "Albums");
            var oldLocalAlbum = OpenLocalAlbum();
            var newLocalAlbum = OpenLocalAlbum();
            PumpUntil(() => newLocalAlbum.IsCompleted); newLocalAlbum.GetAwaiter().GetResult();
            var newLocalRows = grid.ItemsSource;
            PumpUntil(() => oldLocalAlbum.IsCompleted); oldLocalAlbum.GetAwaiter().GetResult();
            Check(ReferenceEquals(grid.ItemsSource, newLocalRows) && (int)Get("_contentLoadingDepth")! == 0,
                "Same-album local replacement republished old rows or leaked loading");
            Console.WriteLine("PASS: same-album local replacement retains current publication");
            Call("ResetDrilldownState", true); Set("_currentTopLevelTag", "Albums");
            var mutableLocalIds = new List<long> { localAlbum };
            var capturedLocal = OpenLocalAlbum(mutableLocalIds);
            mutableLocalIds.Clear();
            Set("_activeLogicalAlbumIds", new List<long>());
            PumpUntil(() => capturedLocal.IsCompleted); capturedLocal.GetAwaiter().GetResult();
            Check(((IEnumerable<ContentRow>)grid.ItemsSource!).Any(), "Local worker reread mutated album IDs");
            Console.WriteLine("PASS: local worker retains copied album IDs after caller/window mutation");
            Call("ResetDrilldownState", true);

            var settings = (AppSettings)Get("_settings")!;
            var server = new OrynivoServerSettings { Id = "journey", Name = "Synthetic source", BaseUrl = "https://journey.invalid" };
            settings.OrynivoServers = [server];
            using var handler = new JourneyHandler(localAlbum);
            using var transport = new HttpClient(handler);
            typeof(OrynivoServerClient).GetField("_http", flags)!.SetValue(Get("_orynivoClient"), transport);
            var selectedSources = (HashSet<string>)Get("_selectedTrackSources")!;
            selectedSources.Clear(); selectedSources.Add("server:journey");
            window.FindControl<TextBox>("SearchTextBox")!.Text = "Journey";
            var search = (Task)Call("ShowSearchResultsAsync", "Journey")!;
            PumpUntil(() => search.IsCompleted); search.GetAwaiter().GetResult();
            var albums = window.FindControl<DataGrid>("SearchAlbumsDataGrid")!;
            var album = ((IEnumerable<ContentRow>)albums.ItemsSource!).Single();
            Check(album.Id == localAlbum && album.OrynivoServer?.Id == "journey", "Search album lost colliding source identity.");
            albums.SelectedItem = album;
            Set("_activeOrynivoServer", server);
            var open = (Task)typeof(MainWindow).GetMethod("OpenOrynivoAlbumTracksAsync", flags, null,
                [typeof(ContentRow)], null)!.Invoke(window, [album])!;
            PumpUntil(() => open.IsCompleted); open.GetAwaiter().GetResult();
            Check(((IEnumerable<ContentRow>)grid.ItemsSource!).Single().Title == "Journey remote track" &&
                window.FindControl<Control>("AlbumDetailHeader")!.IsVisible,
                "Search album did not publish its remote tracks and detail.");
            var history = Get("_navigationStack")!;
            var beforeBack = (int)history.GetType().GetProperty("Count")!.GetValue(history)!;
            Call("BackButton_OnClick", null, new RoutedEventArgs());
            PumpUntil(() => window.FindControl<ScrollViewer>("SearchResultsScrollViewer")!.IsVisible &&
                !((NavigationRestoreOwnership)Get("_navigationRestores")!).IsRestoring);
            Check(albums.SelectedItem is ContentRow restored && restored.Id == localAlbum && restored.OrynivoServer?.Id == "journey" &&
                !window.FindControl<Control>("AlbumDetailHeader")!.IsVisible &&
                (int)history.GetType().GetProperty("Count")!.GetValue(history)! == beforeBack - 1,
                "Actual Back lost search album selection, surface or history.");
            Console.WriteLine("PASS: populated search to colliding remote album to actual Back retains source selection and history");
        }
        finally
        {
            Call("CancelLibrarySearch", true, true);
            Call("ResetDrilldownState", true);
            Dispatcher.UIThread.RunJobs();
        }
    }
}

/// <summary>Serves a populated synthetic album whose ID collides with a local album.</summary>
internal sealed class JourneyHandler : HttpMessageHandler
{
    private readonly long _albumId;
    /// <summary>Creates the source-local collision.</summary>
    /// <param name="albumId">Local and remote album ID.</param>
    internal JourneyHandler(long albumId) => _albumId = albumId;
    /// <summary>Returns synthetic search, metadata and track responses without external transport.</summary>
    /// <param name="request">Synthetic request.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>A complete synthetic response.</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var album = new OrynivoAlbumInfo(_albumId, "Journey album", "Journey artist", null);
        var track = new OrynivoTrackInfo(99001, "remote.flac", null, "remote.flac", "Journey remote track",
            "Journey artist", "Journey artist", "Journey album", Format: "flac");
        var path = request.RequestUri!.AbsolutePath;
        object payload = path == "/api/search/full" ? new OrynivoFullSearchResult([], [album], []) :
            path == $"/api/albums/{_albumId}" ? album :
            path.EndsWith("/tracks", StringComparison.Ordinal) ? new[] { track } : Array.Empty<object>();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
    }
}
