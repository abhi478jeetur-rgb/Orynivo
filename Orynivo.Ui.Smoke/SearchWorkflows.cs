using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orynivo;
using Orynivo.Localization;
using Orynivo.Streaming;

namespace Orynivo.Ui.Smoke;

/// <summary>Exercises production search, recovery and Back with synthetic source collisions.</summary>
internal static class SearchWorkflows
{
    /// <summary>Runs isolated workflows without showing MainWindow or starting playback.</summary>
    internal static void Run()
    {
        var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        void PumpUntil(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TimeSpan.FromSeconds(10))
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Dispatcher.UIThread.RunJobs();
            Check(done(), "Fixture timed out");
        }
        void Finish(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Task Search(string query) => (Task)Call("ShowSearchResultsAsync", query)!;
        Dispatcher.UIThread.RunJobs();
        Call("CancelLibrarySearch", true, true);
        using var handler = new SyntheticSearchHandler();
        using var transport = new HttpClient(handler);
        var client = (OrynivoServerClient)Get("_orynivoClient")!;
        typeof(OrynivoServerClient).GetField("_http", flags)!.SetValue(client, transport);
        var settings = (AppSettings)Get("_settings")!;
        var servers = new[] {
    new OrynivoServerSettings { Id = "one", Name = "One", BaseUrl = "http://one.invalid" },
    new OrynivoServerSettings { Id = "two", Name = "Two", BaseUrl = "http://two.invalid" } };
        settings.OrynivoServers = servers.ToList();
        var selectedSources = (HashSet<string>)Get("_selectedTrackSources")!;
        selectedSources.UnionWith(new[] { "server:one", "server:two" });
        var tracks = window.FindControl<DataGrid>("SearchTracksDataGrid")!;
        var notice = window.FindControl<Border>("LibraryLoadNotice")!;
        var retry = window.FindControl<Button>("LibraryLoadRetryButton")!;
        var empty = window.FindControl<TextBlock>("SearchTracksEmptyTextBlock")!;
        var metadata = (Dictionary<string, ContentRow>)Get("_orynivoTracksByUrl")!;

        try
        {
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.BlockedQuery = "old";
            var old = Search("old");
            PumpUntil(() => handler.Requests.Count(r => r.Query == "old") == 2);
            var newer = Search("new");
            Finish(newer);
            Check(((IEnumerable<ContentRow>)tracks.ItemsSource!).All(row => row.Title == "new"), "New query missing");
            handler.Pending.SetResult();
            Finish(old);
            Check(((IEnumerable<ContentRow>)tracks.ItemsSource!).All(row => row.Title == "new"), "Old query replaced current rows");
            Check(metadata.Values.All(row => row.Title != "old"), "Stale remote metadata was registered");
            Console.WriteLine("PASS: superseded query and delayed metadata registration");

            handler.FailedHosts.Add("two.invalid");
            Finish(Search("partial"));
            Check(notice.IsVisible && ((IEnumerable<ContentRow>)tracks.ItemsSource!).Count() == 1, "Partial results discarded");
            Check(((LibraryLoadResult<ContentRow>)Get("_librarySearchLoadResult")!).Status == LibraryLoadStatus.Partial,
                "Partial status missing");
            foreach (var language in Enum.GetValues<Language>())
            {
                LocalizationManager.Apply(language);
                Call("RefreshLibrarySearchPresentation");
                Check(window.FindControl<TextBlock>("LibraryLoadNoticeMessage")!.Text == string.Format(LocalizationManager.Current.SearchLoadPartial, 1),
                    "Search notice did not change language");
            }
            var previousRows = tracks.ItemsSource;
            handler.FailedHosts.Clear();
            handler.BlockedQuery = "partial";
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Call("LibraryLoadRetryButton_OnClick", retry, new RoutedEventArgs());
            Check(!retry.IsEnabled && ReferenceEquals(previousRows, tracks.ItemsSource), "Retry cleared rows or failed busy state");
            var retryVersion = Get("_librarySearchRetryVersion");
            Call("LibraryLoadRetryButton_OnClick", retry, new RoutedEventArgs());
            Check(Equals(retryVersion, Get("_librarySearchRetryVersion")), "Duplicate retry started");
            handler.Pending.SetResult();
            PumpUntil(() => Get("_librarySearchRetryVersion") is null);
            Check(!notice.IsVisible && ((IEnumerable<ContentRow>)tracks.ItemsSource!).Count() == 2, "Search recovery failed");
            Console.WriteLine("PASS: partial results, seven languages, retained retry rows and recovery");

            selectedSources.Clear(); selectedSources.Add("server:two");
            handler.FailedHosts.Add("two.invalid");
            var requestsBefore = handler.Requests.Count;
            Finish(Search("failed"));
            Check(handler.Requests.Skip(requestsBefore).All(r => r.Host == "two.invalid"), "Excluded source was searched");
            Check(((LibraryLoadResult<ContentRow>)Get("_librarySearchLoadResult")!).Status == LibraryLoadStatus.Failed,
                "Failed search looked empty");
            Check(empty.Text == string.Format(LocalizationManager.Current.SearchLoadFailed, 1), "Failed search said no matches");
            handler.FailedHosts.Clear();
            handler.Empty = true;
            Finish(Search("empty"));
            Check(!notice.IsVisible && empty.Text != string.Format(LocalizationManager.Current.SearchLoadFailed, 1),
                "Successful empty search retained error");
            handler.Empty = false;
            Console.WriteLine("PASS: source filtering, full failure and successful empty search");
            selectedSources.Clear(); selectedSources.Add("server:missing");
            requestsBefore = handler.Requests.Count;
            Finish(Search("excluded"));
            Check(handler.Requests.Count == requestsBefore && !notice.IsVisible &&
                ((LibraryLoadResult<ContentRow>)Get("_librarySearchLoadResult")!).Status == LibraryLoadStatus.Complete,
                "Empty source selection was queried or reported an outage");
            selectedSources.Clear(); selectedSources.Add("server:two");
            Console.WriteLine("PASS: empty source selection is complete without querying excluded sources");

            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.BlockedQuery = "navigate";
            var abandoned = Search("navigate");
            PumpUntil(() => handler.Requests.Any(r => r.Query == "navigate"));
            Call("PushCurrentNavigationState");
            Set("_currentTopLevelTag", "Albums");
            window.FindControl<ScrollViewer>("SearchResultsScrollViewer")!.IsVisible = false;
            var sentinel = new[] { new ContentRow { Id = 999, Title = "Destination" } };
            tracks.ItemsSource = sentinel;
            handler.Pending.SetResult();
            Finish(abandoned);
            Check(ReferenceEquals(tracks.ItemsSource, sentinel) && !notice.IsVisible, "Navigated search replaced destination");
            Check(metadata.Values.All(row => row.Title != "navigate"), "Abandoned metadata was registered");
            Console.WriteLine("PASS: navigation cancellation and abandoned metadata");

            Set("_activeOrynivoServer", servers[1]);
            handler.FailedHosts.Add("two.invalid");
            Finish((Task)Call("ShowOrynivoSearchResultsAsync", "scoped")!);
            Check(((LibraryLoadResult<ContentRow>)Get("_librarySearchLoadResult")!).Status == LibraryLoadStatus.Failed && notice.IsVisible,
                "Scoped server failure missing");
            Console.WriteLine("PASS: server-scoped search shares strict failures");
            var failedResult = Get("_librarySearchLoadResult");
            Call("InvalidateUnifiedLibraryViewCache");
            Check(ReferenceEquals(Get("_librarySearchLoadResult"), failedResult) && notice.IsVisible,
                "Catalog mutation hid a published source failure");
            handler.FailedHosts.Clear();
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.BlockedQuery = "scoped";
            Call("LibraryLoadRetryButton_OnClick", retry, new RoutedEventArgs());
            Call("InvalidateUnifiedLibraryViewCache");
            handler.Pending.SetResult();
            PumpUntil(() => Get("_librarySearchRetryVersion") is null);
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(Get("_librarySearchLoadResult"), failedResult) && notice.IsVisible && retry.IsEnabled,
                "Invalidated retry published rows or disabled recovery");
            Console.WriteLine("PASS: invalidation retains published failure and rejects a pending retry");
            // Generic Back keeps its nested search Reset/Push inside the captured execution.
            handler.Pending = null; handler.BlockedQuery = null;
            Finish(Search("back-search"));
            var searchBox = window.FindControl<TextBox>("SearchTextBox")!;
            searchBox.Text = "back-search";
            tracks.SelectedItem = ((IEnumerable<ContentRow>)tracks.ItemsSource!).First(row => row.OrynivoServer?.Id == servers[1].Id);
            var savedSearch = Call("CaptureCurrentNavigationState")!;
            var backOwner = (NavigationRestoreOwnership)Get("_navigationRestores")!;
            int HistoryCount() => (int)Get("_navigationStack")!.GetType().GetProperty("Count")!.GetValue(Get("_navigationStack"))!;
            var searchHistoryCount = HistoryCount();
            Finish((Task)Call("RestoreNavigationStateAsync", savedSearch)!);
            Check(!backOwner.IsRestoring && backOwner.Context is null && HistoryCount() == searchHistoryCount &&
                tracks.SelectedItem is ContentRow selected && selected.OrynivoServer?.Id == servers[1].Id && selected.Title == "back-search",
                "Generic search Back lost nested history suppression or source-aware selection");
            Console.WriteLine("PASS: generic search Back preserves source selection and nested Reset/Push history suppression");
            handler.BlockedQuery = "back-search";
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchBack = (Task)Call("RestoreNavigationStateAsync", savedSearch)!;
            Check(backOwner.IsRestoring, "Search Back did not hold execution");
            searchBox.Text = "typed-after-back";
            PumpUntil(() => !backOwner.IsRestoring);
            Check(!backOwner.IsRestoring, "User text change retained obsolete Back execution");
            var abandonedSearchGate = handler.Pending;
            handler.Pending = null;
            Finish(Search("typed-after-back"));
            tracks.SelectedItem = null;
            var latestSearchRows = tracks.ItemsSource;
            abandonedSearchGate.SetResult(); Finish(searchBack);
            Check(ReferenceEquals(tracks.ItemsSource, latestSearchRows) && tracks.SelectedItem is null && !backOwner.IsRestoring,
                "Old generic search Back selected or published over a newly typed query");
            Console.WriteLine("PASS: typing a new query abandons generic Back before debounce and rejects old selection");
            Finish((Task)Call("RestoreNavigationStateAsync", savedSearch)!);
            Check(tracks.SelectedItem is ContentRow restoredSearchRow && restoredSearchRow.OrynivoServer?.Id == servers[1].Id && restoredSearchRow.Title == "back-search",
                "Back query text update cancelled its own restore selection");
            Console.WriteLine("PASS: generic Back restores a different saved query without cancelling its own search");

        }
        finally
        {
            handler.Pending?.TrySetResult();
            Call("CancelLibrarySearch", true, true);
            Dispatcher.UIThread.RunJobs();
        }

    }
}

/// <summary>Controls synthetic search failures and late responses without external HTTP.</summary>
internal sealed class SyntheticSearchHandler : HttpMessageHandler
{
    /// <summary>Deliberately cancellation-insensitive response gate.</summary>
    internal TaskCompletionSource? Pending;
    /// <summary>Synthetic query held by the response gate.</summary>
    internal string? BlockedQuery;
    /// <summary>Synthetic sources that return unavailable responses.</summary>
    internal HashSet<string> FailedHosts = [];
    /// <summary>Whether successful responses contain no matches.</summary>
    internal bool Empty;
    /// <summary>In-memory synthetic request observations, never printed.</summary>
    internal List<(string Host, string Query)> Requests = [];
    /// <summary>Returns synthetic search results without external transport.</summary>
    /// <param name="request">Synthetic request.</param>
    /// <param name="token">Cancellation deliberately ignored by the late gate.</param>
    /// <returns>A synthetic response.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath != "/api/search/full") return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var query = Uri.UnescapeDataString(uri.Query.Split('&')[0].Split('=')[1]);
        Requests.Add((uri.Host, query));
        if (query == BlockedQuery && Pending is not null) await Pending.Task; // Intentionally ignore cancellation.
        if (FailedHosts.Contains(uri.Host)) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("synthetic unavailable") };
        var id = query == "old" ? 1 : query == "navigate" ? 3 : 2;
        var track = new OrynivoTrackInfo(id, "synthetic.flac", null, "synthetic.flac", query, "Fixture", "Fixture", "Fixture", Format: "flac");
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(
            new OrynivoFullSearchResult(Empty ? [] : new[] { track }, [], [])))
        };
    }
}
