using System.Diagnostics;
using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Orynivo;
using Orynivo.Streaming;

namespace Orynivo.Ui.Smoke;

/// <summary>Checks production remote album admission and restoration using synthetic HTTP.</summary>
internal static class AlbumWorkflows
{
    /// <summary>Runs held metadata, source snapshots and superseded restoration scenarios.</summary>
    internal static void Run()
    {
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
        var settings = (AppSettings)Get("_settings")!;
        var server = new OrynivoServerSettings { Id = "test", Name = "Synthetic source", BaseUrl = "https://album.invalid" };
        settings.OrynivoServers = [server];
        using var handler = new SyntheticAlbumHandler();
        using var transport = new HttpClient(handler);
        typeof(OrynivoServerClient).GetField("_http", flags)!.SetValue(Get("_orynivoClient"), transport);
        try
        {
            // Exercise actual album open overloads with metadata HTTP held separately from track requests.
            Task OpenAlbumId(long id) => (Task)typeof(MainWindow).GetMethod("OpenOrynivoAlbumTracksAsync", flags, null,
                [typeof(long), typeof(string), typeof(string), typeof(long?), typeof(string)], null)!.Invoke(window, [id, "Captured fallback", null, null, null])!;
            Task OpenAlbumRow(ContentRow albumRow) => (Task)typeof(MainWindow).GetMethod("OpenOrynivoAlbumTracksAsync", flags, null,
                [typeof(ContentRow)], null)!.Invoke(window, [albumRow])!;
            handler.AlbumLookupPayload = "{\"id\":8821,\"album\":\"Metadata fixture\"}";
            foreach (var abandon in new[] { "navigation", "top-level", "configuration", "ambient-server", "removed-server" })
            {
                Call("ResetDrilldownState", true);
                settings.OrynivoServers = [server];
                Set("_activeOrynivoServer", server); Set("_currentTopLevelTag", "OrynivoServer:test:Albums");
                handler.AlbumLookupPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var lookupGate = handler.AlbumLookupPending;
                var tracksBefore = handler.AlbumTrackRequests;
                var oldOpen = OpenAlbumId(8821);
                var lookupToken = ((CancellationTokenSource)Get("_remoteAlbumOpenCts")!).Token;
                if (abandon == "navigation") Call("ResetDrilldownState", true);
                else if (abandon == "configuration") Call("InvalidateCatalogConfiguration");
                else if (abandon == "ambient-server") Set("_activeOrynivoServer", new OrynivoServerSettings { Id = "foreign" });
                else if (abandon == "removed-server") settings.OrynivoServers = [];
                else { var newerView = (Task)Call("ShowTopLevelViewAsync", "Tracks")!; PumpUntil(() => newerView.IsCompleted); newerView.GetAwaiter().GetResult(); }
                if (abandon is "navigation" or "configuration" or "top-level") Check(lookupToken.IsCancellationRequested, "Abandoned album metadata did not receive cancellation");
                var lookupSentinel = new[] { new ContentRow { Title = "New lookup view" } };
                grid.ItemsSource = lookupSentinel;
                lookupGate.SetResult(); PumpUntil(() => oldOpen.IsCompleted); oldOpen.GetAwaiter().GetResult();
                Check(ReferenceEquals(grid.ItemsSource, lookupSentinel) && handler.AlbumTrackRequests == tracksBefore,
                    "Obsolete album lookup started a track/detail request");
                Console.WriteLine("PASS: remote album metadata rejects held " + abandon + " completion before detail admission");
            }
            settings.OrynivoServers = [server]; Set("_activeOrynivoServer", server);
            Call("ResetDrilldownState", true); Set("_currentTopLevelTag", "OrynivoServer:test:Albums");
            handler.AlbumTrackPayload = "[]";
            handler.AlbumLookupPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var duplicateLookupGate = handler.AlbumLookupPending;
            var duplicateOld = OpenAlbumId(8821);
            handler.AlbumLookupPending = null;
            var duplicateNew = OpenAlbumId(8821); PumpUntil(() => duplicateNew.IsCompleted); duplicateNew.GetAwaiter().GetResult();
            var duplicateRows = grid.ItemsSource; var duplicateTrackCalls = handler.AlbumTrackRequests;
            duplicateLookupGate.SetResult(); PumpUntil(() => duplicateOld.IsCompleted); duplicateOld.GetAwaiter().GetResult();
            Check(ReferenceEquals(grid.ItemsSource, duplicateRows) && handler.AlbumTrackRequests == duplicateTrackCalls,
                "Same-album old metadata reopened detail");
            Console.WriteLine("PASS: same-album metadata overlap admits only latest detail");
            Call("ResetDrilldownState", true); Set("_currentTopLevelTag", "OrynivoServer:test:Albums");
            Set("_activeArtistFilterId", (long?)701); Set("_activeArtistFilterName", "Captured artist");
            handler.AlbumLookupPayload = "null"; handler.AlbumPayload = "[]";
            handler.AlbumLookupPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var scopeGate = handler.AlbumLookupPending;
            var scopedLookup = OpenAlbumId(9921);
            Set("_activeArtistFilterId", (long?)702); Set("_activeArtistFilterName", "Changed artist");
            scopeGate.SetResult(); PumpUntil(() => scopedLookup.IsCompleted); scopedLookup.GetAwaiter().GetResult();
            Check((long?)Get("_activeArtistFilterId") == 701 && (string?)Get("_activeArtistFilterName") == "Captured artist" &&
                (string?)Get("_activeAlbumFilterTitle") == "Captured fallback", "Scalar album lookup reread ambient artist/fallback state");
            Console.WriteLine("PASS: scalar metadata lookup retains captured artist scope and fallback");
            Call("ResetDrilldownState", true); Set("_currentTopLevelTag", "OrynivoServer:test:Albums");
            handler.AlbumLookupPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var rowGate = handler.AlbumLookupPending;
            var mutableAlbumRow = new ContentRow { Id = 9922, Title = "Captured row fallback", Artist = "Captured row artist", LogicalAlbumIds = new List<long> { 9922 } };
            var rowLookup = OpenAlbumRow(mutableAlbumRow);
            mutableAlbumRow.Title = "Mutated row";
            ((List<long>)mutableAlbumRow.LogicalAlbumIds!).Add(9923);
            rowGate.SetResult(); PumpUntil(() => rowLookup.IsCompleted); rowLookup.GetAwaiter().GetResult();
            Check((string?)Get("_activeAlbumFilterTitle") == "Captured row fallback" &&
                ((IReadOnlyList<long>)Get("_activeLogicalAlbumIds")!).SequenceEqual(new long[] { 9922 }), "Row fallback or album IDs were reread after await");
            Console.WriteLine("PASS: row metadata lookup snapshots mutable fallback and album IDs");
            handler.AlbumLookupPending = null;
            Call("ResetDrilldownState", true); Set("_currentTopLevelTag", "OrynivoServer:test:Albums");
            handler.AlbumLookupPayload = "{\"id\":8821,\"album\":\"Restore fixture\"}";
            handler.AlbumLookupPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var restoreLookupGate = handler.AlbumLookupPending;
            var restoreLookupCount = handler.AlbumLookupRequests;
            Task RestoreAlbum() => (Task)Call("RestoreOrynivoAlbumTracksAsync", "OrynivoServer:test:Albums", 8821L, "Restore fallback", null, null, 0d, null)!;
            var heldRestore = RestoreAlbum(); PumpUntil(() => handler.AlbumLookupRequests > restoreLookupCount);
            handler.AlbumLookupPending = null;
            var latestRestore = RestoreAlbum(); PumpUntil(() => latestRestore.IsCompleted); latestRestore.GetAwaiter().GetResult();
            var restoredRows = grid.ItemsSource; var restoredTrackCalls = handler.AlbumTrackRequests;
            restoreLookupGate.SetResult(); PumpUntil(() => heldRestore.IsCompleted); heldRestore.GetAwaiter().GetResult();
            Check(ReferenceEquals(grid.ItemsSource, restoredRows) && handler.AlbumTrackRequests == restoredTrackCalls,
                "Same-tag restore metadata admitted obsolete detail");
            Console.WriteLine("PASS: same-tag album restore metadata retains latest request ownership");
            Call("ResetDrilldownState", true);
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var overviewGate = handler.Pending;
            var overviewLookupCount = handler.AlbumLookupRequests;
            var heldOverviewRestore = RestoreAlbum();
            handler.Pending = null;
            var latestOverview = (Task)Call("ShowTopLevelViewAsync", "OrynivoServer:test:Albums")!;
            PumpUntil(() => latestOverview.IsCompleted); latestOverview.GetAwaiter().GetResult();
            var overviewRows = grid.ItemsSource;
            overviewGate.SetResult(); PumpUntil(() => heldOverviewRestore.IsCompleted); heldOverviewRestore.GetAwaiter().GetResult();
            Check(ReferenceEquals(grid.ItemsSource, overviewRows) && handler.AlbumLookupRequests == overviewLookupCount,
                "Obsolete same-tag overview restore entered metadata lookup");
            Console.WriteLine("PASS: exact overview token rejects same-tag restore before metadata admission");
            Call("ResetDrilldownState", true);
            handler.Pending = null; handler.AlbumLookupPending = null;

            var beforeForeignAlbumRequests = handler.AlbumLookupRequests;
            var foreignAlbumTask = OpenAlbumRow(new ContentRow { Id = 8821, OrynivoServer = new OrynivoServerSettings { Id = "foreign" } });
            PumpUntil(() => foreignAlbumTask.IsCompleted); foreignAlbumTask.GetAwaiter().GetResult();
            Check(handler.AlbumLookupRequests == beforeForeignAlbumRequests, "Foreign album row queried ambient server");
            Console.WriteLine("PASS: source-bound album row rejects conflicting ambient server before HTTP");

            var copiedRowServer = new OrynivoServerSettings { Id = server.Id, BaseUrl = "http://snapshot.invalid" };
            Set("_activeOrynivoServer", copiedRowServer);
            var copiedRowOpen = OpenAlbumRow(new ContentRow { Id = 8821, OrynivoServer = copiedRowServer });
            PumpUntil(() => copiedRowOpen.IsCompleted); copiedRowOpen.GetAwaiter().GetResult();
            Check(ReferenceEquals(Get("_activeOrynivoServer"), server), "Copied catalog row did not resolve current configured owner");
            Set("_activeOrynivoServer", copiedRowServer);
            var copiedScalarOpen = OpenAlbumId(8821);
            PumpUntil(() => copiedScalarOpen.IsCompleted); copiedScalarOpen.GetAwaiter().GetResult();
            Check(ReferenceEquals(Get("_activeOrynivoServer"), server), "Copied scalar source did not resolve configured owner");
            Console.WriteLine("PASS: copied catalog sources resolve current configured server for both open routes");
            Call("ResetDrilldownState", true);

        }
        finally
        {
            handler.ReleaseGates();
            Call("ResetDrilldownState", true);
            Dispatcher.UIThread.RunJobs();
        }
    }
}

/// <summary>Returns synthetic album metadata and empty tracks while allowing deliberate late responses.</summary>
internal sealed class SyntheticAlbumHandler : HttpMessageHandler
{
    /// <summary>Gets or sets the complete synthetic metadata payload.</summary>
    internal string AlbumLookupPayload { get; set; } = "null";
    /// <summary>Gets or sets the cancellation-insensitive metadata gate.</summary>
    internal TaskCompletionSource? AlbumLookupPending { get; set; }
    /// <summary>Gets or sets the cancellation-insensitive overview gate.</summary>
    internal TaskCompletionSource? Pending { get; set; }
    /// <summary>Gets the metadata request count.</summary>
    internal int AlbumLookupRequests { get; private set; }
    /// <summary>Gets the track request count.</summary>
    internal int AlbumTrackRequests { get; private set; }
    /// <summary>Gets or sets the synthetic empty track payload.</summary>
    internal string AlbumTrackPayload { get; set; } = "[]";
    /// <summary>Gets or sets the synthetic overview payload.</summary>
    internal string AlbumPayload { get; set; } = "[]";
    private readonly List<TaskCompletionSource> _gates = [];

    /// <summary>Releases every captured gate, including gates superseded by newer requests.</summary>
    internal void ReleaseGates()
    {
        foreach (var gate in _gates) gate.TrySetResult();
    }

    /// <summary>Holds captured synthetic responses without making any external request.</summary>
    /// <param name="request">Synthetic server request.</param>
    /// <param name="cancellationToken">Cancellation deliberately ignored for ownership regressions.</param>
    /// <returns>The synthetic response.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith("/api/albums/", StringComparison.Ordinal) && long.TryParse(path.Split('/').Last(), out _))
        {
            AlbumLookupRequests++;
            var payload = AlbumLookupPayload;
            if (AlbumLookupPending is { } gate) { _gates.Add(gate); await gate.Task; }
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
        if (path.StartsWith("/api/albums/", StringComparison.Ordinal) && path.EndsWith("/tracks", StringComparison.Ordinal))
            AlbumTrackRequests++;
        if (Pending is { } pending) { _gates.Add(pending); await pending.Task; }
        var response = path == "/api/albums" ? AlbumPayload : path.EndsWith("/tracks", StringComparison.Ordinal) ? AlbumTrackPayload : "[]";
        return new(HttpStatusCode.OK) { Content = new StringContent(response) };
    }
}
