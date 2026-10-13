using System.Diagnostics;
using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Orynivo;
using Orynivo.Library;
using Orynivo.Localization;
using Orynivo.Streaming;

namespace Orynivo.Ui.Smoke;

/// <summary>Exercises restart previews and background refresh through real window adapters.</summary>
internal static class ViewRefreshWorkflows
{
    /// <summary>Runs held source, unchanged publication, invalidation, restart and navigation checks.</summary>
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var windows = new List<MainWindow>();
        using var handler = new ViewRefreshHandler();
        using var transport = new HttpClient(handler);
        MainWindow Create()
        {
            var window = new MainWindow();
            windows.Add(window);
            var settings = (AppSettings)Get(window, "_settings")!;
            settings.GenreCloudBackground = GenreCloudBackgroundMode.None;
            settings.ReduceMotion = true;
            settings.OrynivoServers = [new OrynivoServerSettings
                { Id = "preview", Name = "Synthetic preview", BaseUrl = "https://preview.invalid", ApiKey = "synthetic-preview-secret" }];
            typeof(OrynivoServerClient).GetField("_http", flags)!.SetValue(Get(window, "_orynivoClient"), transport);
            Dispatcher.UIThread.RunJobs();
            return window;
        }
        object? Get(MainWindow window, string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        object? Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Pump(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TimeSpan.FromSeconds(15))
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Dispatcher.UIThread.RunJobs();
            Check(done(), "View refresh workflow timed out.");
        }
        void Finish(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Task Show(MainWindow window, string tag) => (Task)Call(window, "ShowTopLevelViewAsync", tag)!;
        ContentControl[] Sections(MainWindow window) => (ContentControl[])Get(window, "_dashboardSections")!;
        try
        {
            var window = Create();
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = handler.Requests;
            var dashboard = Show(window, "Dashboard");
            Pump(() => handler.Requests > before && Sections(window)[3].Content is not null);
            Check(!dashboard.IsCompleted && Sections(window)[0].Content is not null &&
                !window.FindControl<Border>("ContentLoadingOverlay")!.IsHitTestVisible,
                "Cold Dashboard did not publish its hero/statistics ahead of held remote data.");
            handler.Pending.SetResult(); Finish(dashboard);
            Check(Sections(window).All(section => section.Content is not null), "Dashboard sections failed to publish.");
            Finish((Task)Get(window, "_previewWriteTask")!);
            Console.WriteLine("PASS: Dashboard shell and local statistics publish before held remote sections");

            var media = Sections(window)[1].Content;
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            var repeat = Show(window, "Dashboard"); Pump(() => handler.Requests > before);
            Check(ReferenceEquals(media, Sections(window)[1].Content), "Dashboard discarded cached media during refresh.");
            handler.Pending.SetResult(); Finish(repeat);
            Check(ReferenceEquals(media, Sections(window)[1].Content), "Unchanged Dashboard media was rebuilt.");

            var settings = (AppSettings)Get(window, "_settings")!;
            var originalLanguage = settings.Language;
            settings.Language = originalLanguage == Language.English ? Language.German : Language.English;
            LocalizationManager.Apply(settings.Language);
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            var localized = Show(window, "Dashboard"); Pump(() => handler.Requests > before);
            Check(!localized.IsCompleted && !ReferenceEquals(media, Sections(window)[1].Content),
                "Retained Dashboard did not rebuild changed presentation before remote data completed.");
            handler.Pending.SetResult(); Finish(localized);
            settings.Language = originalLanguage; LocalizationManager.Apply(originalLanguage);
            handler.Pending = null;
            Finish(Show(window, "RecentAlbumsAll"));
            var root = (StackPanel)Get(window, "_dashboardRootPanel")!;
            var showAllContent = root.Children.First();
            Finish((Task)Call(window, "BuildDashboardAsync")!);
            Check(ReferenceEquals(showAllContent, root.Children.First()),
                "Dashboard refresh admission replaced the shared Show all page.");
            Console.WriteLine("PASS: cached Dashboard remains interactive and unchanged sections retain controls");

            handler.Pending = null;
            Finish(Show(window, "GenreCloud")); Finish((Task)Get(window, "_previewWriteTask")!);
            var localFacets = Get(window, "_genreCloudLocalFacets");
            Check(((List<ContentRow>)Get(window, "_genreCloudAlbumRows")!).Any(row => row.OrynivoServer?.Id == "preview" && row.AlbumId == 80001),
                "Targeted cloud album resolution lost the selected server album.");
            var nodes = (List<GenreCloudNode>)Get(window, "_genreCloudNodes")!;
            Check(nodes.Any(node => node.Key == "rock"), "Synthetic cloud failed to publish.");
            var loadingPanel = window.FindControl<Border>("GenreCloudLoadingPanel")!;
            var display = window.FindControl<Grid>("GenreCloudDisplay")!;
            var progressBar = window.FindControl<ProgressBar>("GenreCloudLoadingProgressBar")!;
            var genreButton = window.FindControl<Canvas>("GenreCloudNodesCanvas")!.Children
                .OfType<Button>().First(button => button.Tag as string == "rock");
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            var click = new RetryCompletion();
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(click);
            try { genreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            Check(loadingPanel.IsVisible && !display.IsVisible && !window.FindControl<DataGrid>("ContentDataGrid")!.IsVisible,
                "Cloud click did not immediately acknowledge admission and hide previous content.");
            var timerBudget = Stopwatch.StartNew();
            while ((progressBar.IsIndeterminate || progressBar.Value <= 0) && timerBudget.Elapsed < TimeSpan.FromSeconds(15))
            {
                using var cycle = new CancellationTokenSource(50);
                Dispatcher.UIThread.MainLoop(cycle.Token);
            }
            Check(!progressBar.IsIndeterminate && progressBar.Value > 0 && progressBar.Value < 100 && !click.IsCompleted,
                "Held cloud assembly did not expose completed-step progress.");
            handler.Pending.SetResult(); Pump(() => click.IsCompleted);
            Check(!loadingPanel.IsVisible && display.IsVisible, "Completed cloud assembly retained its loading panel.");

            // A memory-cached branch must render immediately without a blocking panel.
            Finish((Task)Call(window, "ShowGenreCloudAsync", "rock", null)!);
            Check(!loadingPanel.IsVisible, "Cached branch selection unnecessarily blocked interaction.");
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            var abandonedBranch = (Task)Call(window, "ShowGenreCloudAsync", "jazz", null)!;
            Check(loadingPanel.IsVisible, "New uncached branch did not show feedback.");
            Pump(() => handler.Requests > before);
            Finish(Show(window, "Queue"));
            Check(!loadingPanel.IsVisible, "Navigation left cloud feedback active.");
            handler.Pending.SetResult(); Finish(abandonedBranch);
            Check(!loadingPanel.IsVisible && window.FindControl<DataGrid>("ContentDataGrid")!.IsVisible,
                "Late cloud cleanup hid the destination view.");
            handler.Pending = null; Finish(Show(window, "GenreCloud"));
            Check(ReferenceEquals(localFacets, Get(window, "_genreCloudLocalFacets")),
                "Cloud navigation reread unchanged local facets.");
            Check(handler.AlbumBatches > 0 && handler.FullAlbums == 0,
                "Cloud assembly transferred a complete album catalog.");
            Console.WriteLine("PASS: cloud click immediately hides old content, reports real work steps and cleans up on completion/navigation");

            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            Call(window, "InvalidateGenreCloudViewCache");
            Check(Get(window, "_genreCloudLocalFacets") is null, "Catalog invalidation retained old facet input.");
            var cloud = Show(window, "GenreCloud"); Pump(() => handler.Requests > before);
            var canvas = window.FindControl<Canvas>("GenreCloudNodesCanvas")!;
            var firstButton = canvas.Children.FirstOrDefault();
            Check(canvas.Children.Count > 0 && !cloud.IsCompleted, "Invalidation discarded the retained cloud.");
            handler.Pending.SetResult(); Finish(cloud);
            Check(ReferenceEquals(firstButton, canvas.Children.FirstOrDefault()), "Unchanged cloud revalidation rebuilt nodes.");
            Console.WriteLine("PASS: invalidated cloud renders cached nodes immediately and retains unchanged controls");

            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            handler.NodeCount = 99; Call(window, "InvalidateGenreCloudViewCache");
            var changed = Show(window, "GenreCloud"); Pump(() => handler.Requests > before);
            Check(((List<GenreCloudNode>)Get(window, "_genreCloudNodes")!).First(node => node.Key == "rock").TrackCount < 99,
                "Cloud published held fresh counts prematurely.");
            handler.Pending.SetResult(); Finish(changed);
            Check(((List<GenreCloudNode>)Get(window, "_genreCloudNodes")!).First(node => node.Key == "rock").TrackCount >= 99,
                "Changed cloud counts did not replace retained data.");
            Finish((Task)Get(window, "_previewWriteTask")!);
            Console.WriteLine("PASS: changed cloud data replaces its cached presentation after completion");

            handler.Pending = null; handler.Fail = true;
            Call(window, "InvalidateGenreCloudViewCache");
            Finish(Show(window, "GenreCloud"));
            Check(((List<GenreCloudNode>)Get(window, "_genreCloudNodes")!).Any(node => node.Key == "rock" && node.TrackCount >= 99),
                "Unavailable source replaced retained cloud data with empty results.");
            handler.Fail = false;
            Finish(Show(window, "GenreCloud"));
            var coldUnavailable = Create();
            ((AppSettings)Get(coldUnavailable, "_settings")!).OrynivoServers![0].Id = "preview-cold";
            handler.Fail = true;
            Finish(Show(coldUnavailable, "GenreCloud"));
            var partialRows = (List<ContentRow>)Get(coldUnavailable, "_genreCloudTrackRows")!;
            var coldNodes = (List<GenreCloudNode>)Get(coldUnavailable, "_genreCloudNodes")!;
            Check(partialRows.All(row => row.OrynivoServer is null) &&
                ((System.Collections.IDictionary)Get(coldUnavailable, "_genreCloudViewCache")!).Count == 0 &&
                (coldNodes.Count > 0 || coldUnavailable.FindControl<TextBlock>("GenreCloudEmptyTextBlock")!.IsVisible),
                "Cold failed cloud did not publish usable local data without complete-cache persistence.");
            handler.Fail = false;
            Console.WriteLine("PASS: failed cloud revalidation preserves retained data and permits recovery");

            // A new window has no session cache, simulating restart with the same synthetic settings.
            var restarted = Create();
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            var restartDashboard = Show(restarted, "Dashboard");
            Pump(() => handler.Requests > before && Sections(restarted).All(section => section.Content is not null));
            Check(!restartDashboard.IsCompleted, "Restart Dashboard awaited network before restoring disk previews.");
            handler.Pending.SetResult(); Finish(restartDashboard);
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); before = handler.Requests;
            var restartCloud = Show(restarted, "GenreCloud"); Pump(() => handler.Requests > before);
            Check(((List<GenreCloudNode>)Get(restarted, "_genreCloudNodes")!).Any(node => node.Key == "rock" && node.TrackCount >= 99),
                "Restart cloud did not restore its credential-free preview before network completion.");
            var restoredTrack = ((List<ContentRow>)Get(restarted, "_genreCloudTrackRows")!)
                .FirstOrDefault(row => row.OrynivoServer?.Id == "preview");
            Check(restoredTrack?.Id == 70001 && restoredTrack.FilePath.Contains("synthetic-preview-secret", StringComparison.Ordinal),
                "Restart preview did not reconstruct current-source playback metadata in memory.");
            Finish(Show(restarted, "Queue"));
            var destination = restarted.FindControl<DataGrid>("ContentDataGrid")!.ItemsSource;
            handler.Pending.SetResult(); Finish(restartCloud);
            Check(ReferenceEquals(destination, restarted.FindControl<DataGrid>("ContentDataGrid")!.ItemsSource) &&
                !restarted.FindControl<Border>("GenreCloudPanel")!.IsVisible,
                "Late cloud completion overwrote independent navigation.");
            Console.WriteLine("PASS: restart previews precede network data and obsolete refresh cannot overwrite navigation");

            foreach (var owner in windows) Finish((Task)Get(owner, "_previewWriteTask")!);
            var cache = Path.Combine(AppPaths.DataRoot, "view-previews-v1");
            Check(Directory.GetFiles(cache, "*.json").All(path =>
            {
                var bytes = File.ReadAllText(path);
                return !bytes.Contains("synthetic-preview-secret", StringComparison.Ordinal) &&
                    !bytes.Contains("preview.invalid", StringComparison.Ordinal) && !bytes.Contains("?key=", StringComparison.Ordinal);
            }), "Persisted preview contains connection credentials or authenticated URLs.");
            Call(restarted, "InvalidateCatalogConfiguration"); Finish((Task)Get(restarted, "_previewResetTask")!);
            Check(Sections(restarted).All(section => section.Content is null) && Directory.GetFiles(cache, "*.json").Length == 0,
                "Configuration change retained previous profile/source previews.");
            Console.WriteLine("PASS: preview persistence excludes secrets and configuration changes clear retained data");
        }
        finally
        {
            handler.Pending?.TrySetResult();
            handler.Fail = false;
            foreach (var window in windows)
            {
                Call(window, "ResetDrilldownState", true);
                var pending = ((System.Collections.IDictionary)Get(window, "_genreCloudViewLoads")!).Values
                    .Cast<Task>().Concat(((System.Collections.IDictionary)Get(window, "_dashboardRefreshTasks")!).Values.Cast<Task>())
                    .ToArray();
                var settled = Task.WhenAll(pending);
                Pump(() => settled.IsCompleted);
                _ = settled.Exception;
                Finish((Task)Get(window, "_previewWriteTask")!);
            }
            Dispatcher.UIThread.RunJobs();
        }
    }
}

/// <summary>Holds synthetic view responses without contacting external services.</summary>
internal sealed class ViewRefreshHandler : HttpMessageHandler
{
    private int _requests;
    private int _albumBatches;
    private int _fullAlbums;
    /// <summary>Gets selected album batch requests.</summary>
    internal int AlbumBatches => Volatile.Read(ref _albumBatches);
    /// <summary>Gets complete album catalog requests.</summary>
    internal int FullAlbums => Volatile.Read(ref _fullAlbums);
    /// <summary>Gets the number of admitted synthetic requests.</summary>
    internal int Requests => Volatile.Read(ref _requests);
    /// <summary>Gets or sets an optional deterministic response gate.</summary>
    internal TaskCompletionSource? Pending { get; set; }
    /// <summary>Gets or sets the refreshed genre count.</summary>
    internal int NodeCount { get; set; } = 3;
    /// <summary>Gets or sets whether synthetic source requests fail.</summary>
    internal bool Fail { get; set; }
    /// <summary>Returns bounded synthetic metadata after the captured gate.</summary>
    /// <param name="request">Synthetic source request.</param>
    /// <param name="cancellationToken">Ignored to exercise obsolete responses.</param>
    /// <returns>The controlled synthetic response.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        var pending = Pending; var count = NodeCount;
        if (pending is not null) await pending.Task;
        if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/albums/by-ids") Interlocked.Increment(ref _albumBatches);
        if (path == "/api/albums") Interlocked.Increment(ref _fullAlbums);
        var parent = request.RequestUri.Query.TrimStart('?').Split('&')
            .FirstOrDefault(parameter => parameter.StartsWith("parent=", StringComparison.Ordinal))?[7..];
        var parentJson = parent is null ? "null" : "\"" + Uri.UnescapeDataString(parent) + "\"";
        var payload = path switch
        {
            "/api/genres/cloud" => "{\"parentKey\":" + parentJson + ",\"breadcrumbKeys\":[],\"nodes\":[{\"key\":\"rock\",\"displayName\":\"Rock\",\"trackCount\":" + count +
                ",\"albumCount\":1,\"hasChildren\":true}],\"candidates\":[{\"trackId\":70001,\"genreKey\":\"rock\",\"isFavorite\":false}]}",
            "/api/tracks/by-ids" => "[{\"id\":70001,\"path\":\"synthetic-track.flac\",\"fileName\":\"synthetic-track.flac\",\"title\":\"Synthetic cloud track\",\"artist\":\"Synthetic source\",\"genre\":\"Rock\",\"albumId\":80001}]",
            "/api/albums/by-ids" => "[{\"id\":80001,\"album\":\"Synthetic selected album\",\"displayArtist\":\"Synthetic source\"}]",
            "/api/library/summary" => "{\"albumCount\":1,\"trackCount\":3,\"artistCount\":1,\"favoriteCount\":0}",
            "/api/scan" => "{}",
            _ => "[]"
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
    }
}
