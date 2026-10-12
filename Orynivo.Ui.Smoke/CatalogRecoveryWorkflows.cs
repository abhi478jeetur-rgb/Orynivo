using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orynivo;
using Orynivo.Library;
using Orynivo.Streaming;

namespace Orynivo.Ui.Smoke;

/// <summary>Exercises actual unified catalog retry, navigation and configuration recovery.</summary>
internal static class CatalogRecoveryWorkflows
{
    /// <summary>Runs deterministic retry lifetimes with synthetic source failures and gates.</summary>
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
        var notice = window.FindControl<Border>("LibraryLoadNotice")!;
        var button = window.FindControl<Button>("LibraryLoadRetryButton")!;
        window.FindControl<ScrollViewer>("SearchResultsScrollViewer")!.IsVisible = false;
        grid.IsVisible = true;
        var server = new OrynivoServerSettings { Id = "recovery", Name = "Synthetic recovery", BaseUrl = "https://recovery.invalid" };
        ((AppSettings)Get("_settings")!).OrynivoServers = [server];
        using var handler = new CatalogRecoveryHandler();
        using var transport = new HttpClient(handler);
        typeof(OrynivoServerClient).GetField("_http", flags)!.SetValue(Get("_orynivoClient"), transport);
        Task Bind() => (Task)Call("BindLocalRowsAndStartRemoteAppendAsync", "Tracks", false)!;
        void Finish(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        RetryCompletion Retry()
        {
            var completion = new RetryCompletion();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(completion);
            try { Call("LibraryLoadRetryButton_OnClick", button, new RoutedEventArgs()); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            return completion;
        }
        try
        {
            Set("_currentTopLevelTag", "Tracks");
            handler.Fail = true;
            Finish(Bind());
            Check(notice.IsVisible && ((LibraryLoadResult<ContentRow>)Get("_unifiedLibraryLoadResult")!).Status == LibraryLoadStatus.Partial,
                "Initial unavailable source did not retain local rows and a partial notice.");
            var originalRows = grid.ItemsSource;
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldRetry = Retry();
            Check(!button.IsEnabled && ReferenceEquals(originalRows, grid.ItemsSource), "Retry lost rows or busy state.");
            var version = Get("_unifiedLibraryRetryVersion");
            var duplicate = Retry();
            Check(duplicate.IsCompleted && Equals(version, Get("_unifiedLibraryRetryVersion")), "Duplicate retry admitted another load.");
            handler.Pending.SetResult(); PumpUntil(() => heldRetry.IsCompleted);
            Check(notice.IsVisible && button.IsEnabled, "Failed retry did not allow recovery.");
            handler.Pending = null; handler.Fail = false;
            var recovered = Retry(); PumpUntil(() => recovered.IsCompleted);
            Check(!notice.IsVisible && button.IsEnabled, "Recovered source retained a failure or busy button.");
            Console.WriteLine("PASS: actual catalog retry retains rows, suppresses duplicates and recovers after failure");

            Call("InvalidateUnifiedLibraryViewCache"); handler.Fail = true; Finish(Bind());
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandonedRetry = Retry();
            Call("PushCurrentNavigationState"); Set("_currentTopLevelTag", "Albums");
            var sentinel = new[] { new ContentRow { Id = 90001, Title = "Synthetic destination" } };
            grid.ItemsSource = sentinel;
            handler.Pending.SetResult(); PumpUntil(() => abandonedRetry.IsCompleted);
            Check(ReferenceEquals(grid.ItemsSource, sentinel) && !notice.IsVisible, "Late retry overwrote navigation.");
            Console.WriteLine("PASS: completed obsolete catalog retry cannot overwrite a newer navigation surface");

            handler.Pending = null; Set("_currentTopLevelTag", "Tracks");
            Call("InvalidateUnifiedLibraryViewCache"); Finish(Bind());
            var incomplete = Get("_unifiedLibraryLoadResult"); originalRows = grid.ItemsSource;
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var invalidatedRetry = Retry();
            Call("InvalidateUnifiedLibraryViewCache");
            handler.Pending.SetResult(); PumpUntil(() => invalidatedRetry.IsCompleted);
            Check(ReferenceEquals(incomplete, Get("_unifiedLibraryLoadResult")) && ReferenceEquals(originalRows, grid.ItemsSource) && button.IsEnabled,
                "Invalidated retry replaced rows/outcome or disabled recovery.");
            Console.WriteLine("PASS: cache invalidation rejects held retry publication and resets retry availability");

            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); handler.Fail = false;
            var earlier = Bind();
            var cancellation = (CancellationTokenSource)Get("_unifiedLibraryAppendCts")!;
            var owner = (LibraryCatalogLoadCoordinator<ContentRow>)Get("_unifiedLibraryLoads")!;
            var originalVersion = owner.Version;
            Task? reentrant = null;
            using (var registration = cancellation.Token.Register(() => reentrant = Bind()))
            {
                var interrupted = Bind();
                Check(reentrant is not null && interrupted.IsCompleted && owner.Version == originalVersion + 2 &&
                    !((CancellationTokenSource)Get("_unifiedLibraryAppendCts")!).IsCancellationRequested,
                    "Cancellation callback lost newer admission or cancellation token.");
                handler.Pending.SetResult(); Finish(earlier); Finish(interrupted); Finish(reentrant!);
            }
            Check(((LibraryLoadResult<ContentRow>)Get("_unifiedLibraryLoadResult")!).Status == LibraryLoadStatus.Complete,
                "Newest reentrant catalog did not publish.");
            Console.WriteLine("PASS: cancellation callback retains newest catalog request, token and publication");

            Call("InvalidateCatalogConfiguration");
            var generation = owner.Generation; var published = Get("_unifiedLibraryLoadResult");
            handler.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldConfiguration = Bind(); server.StreamingFormat = "opus";
            Call("InvalidateCatalogConfiguration");
            Check(owner.Generation == generation + 1, "Configuration identity did not advance.");
            handler.Pending.SetResult(); Finish(oldConfiguration);
            Check(ReferenceEquals(published, Get("_unifiedLibraryLoadResult")), "Old configuration published after invalidation.");
            handler.Pending = null; Finish(Bind());
            Check(((LibraryLoadResult<ContentRow>)Get("_unifiedLibraryLoadResult")!).Status == LibraryLoadStatus.Complete,
                "New configuration could not recover.");
            Console.WriteLine("PASS: configuration change rejects held catalog publication and permits fresh recovery");
        }
        finally
        {
            handler.ReleaseGates();
            Call("ResetDrilldownState", true);
            Dispatcher.UIThread.RunJobs();
        }
    }
}

/// <summary>Observes async-void retry completion through its synchronization-context lifetime.</summary>
internal sealed class RetryCompletion : SynchronizationContext
{
    private int _operations;
    private bool _started;
    /// <summary>Gets whether the actual retry handler and its finally block have completed.</summary>
    internal bool IsCompleted => _started && _operations == 0;
    /// <summary>Records admission of the actual async-void handler.</summary>
    public override void OperationStarted() { _started = true; _operations++; }
    /// <summary>Records completion after the handler's finally block.</summary>
    public override void OperationCompleted() => _operations--;
    /// <summary>Runs continuations on the owning UI dispatcher while retaining this observation context.</summary>
    /// <param name="callback">Retry continuation.</param>
    /// <param name="state">Continuation state.</param>
    public override void Post(SendOrPostCallback callback, object? state) => Dispatcher.UIThread.Post(() =>
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try { callback(state); }
        finally { SetSynchronizationContext(previous); }
    });
}

/// <summary>Controls catalog source availability without using an external transport.</summary>
internal sealed class CatalogRecoveryHandler : HttpMessageHandler
{
    /// <summary>Gets or sets whether the synthetic source is unavailable.</summary>
    internal bool Fail { get; set; }
    /// <summary>Gets or sets an intentionally cancellation-insensitive gate.</summary>
    internal TaskCompletionSource? Pending { get; set; }
    /// <summary>Retains captured gates safely if catalog adapters overlap on worker threads.</summary>
    private readonly ConcurrentBag<TaskCompletionSource> _gates = [];
    /// <summary>Releases every captured gate during teardown.</summary>
    internal void ReleaseGates() { foreach (var gate in _gates) gate.TrySetResult(); }
    /// <summary>Returns synthetic empty or unavailable responses after an optional late gate.</summary>
    /// <param name="request">Synthetic source request.</param>
    /// <param name="cancellationToken">Cancellation ignored by the deliberate late response gate.</param>
    /// <returns>A wholly synthetic response.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Pending is { } gate) { _gates.Add(gate); await gate.Task; }
        return new(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
        { Content = new StringContent(Fail ? "synthetic unavailable" : "[]") };
    }
}
