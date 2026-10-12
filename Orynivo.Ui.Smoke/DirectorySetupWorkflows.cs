using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orynivo.Library;
using Orynivo.Localization;

namespace Orynivo.Ui.Smoke;

/// <summary>Checks initial scan admission and row lifetime with a synthetic scanner.</summary>
internal static class DirectorySetupWorkflows
{
    /// <summary>Exercises new folders, duplicates, concurrent rows, cancellation and failure without scanning media.</summary>
    /// <param name="root">Process-owned temporary data root.</param>
    internal static void Run(string root)
    {
        var first = Path.Combine(root, "setup-first");
        var second = Path.Combine(root, "setup-second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var settings = new SettingsStore().Load();
        settings.LibraryPaths.Clear();
        settings.CalculateMissingReplayGainDuringScan = false;
        List<string>? published = null;
        var view = new SettingsView(settings, paths => published = paths);
        var panel = view.FindControl<StackPanel>("DirectoriesPanel")!;
        var gate = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var changed = 0;
        CancellationToken firstToken = default;
        IProgress<ScanProgress>? firstProgress = null;
        view.LocalLibraryChanged += () => changed++;
        view.DirectoryScanner = (path, progress, replayGain, token) =>
        {
            Check(published?.Contains(path) == true, "Initial scan preceded source publication.");
            Check(!replayGain, "Initial scan ignored the ReplayGain preference.");
            calls++;
            if (path == first)
            {
                firstToken = token;
                firstProgress = progress;
                return gate.Task;
            }
            return Task.FromResult(new ScanResult(1, 1, 0, 0, 0));
        };
        try
        {
            var addFirst = view.AddLibraryDirectoryAsync(first);
            Check(calls == 1 && !addFirst.IsCompleted, "A new directory did not immediately start its scan.");
            var firstRow = (StackPanel)panel.Children.Single();
            var firstGrid = (Grid)firstRow.Children[0];
            var scan = (Button)firstGrid.Children[2];
            var metadata = (Button)firstGrid.Children[3];
            var status = (TextBlock)firstRow.Children[1];
            Check(status.IsVisible && Equals(scan.Content, LocalizationManager.Current.Cancel) && !metadata.IsEnabled,
                "Automatic scan did not expose progress and cancellation.");
            firstProgress!.Report(new ScanProgress(1, 2, "synthetic.wav"));
            Dispatcher.UIThread.RunJobs();
            Check(status.Text?.StartsWith("1/2") == true, "Automatic scan progress was not rendered.");
            Check(!Finish(view.AddLibraryDirectoryAsync(first.ToUpperInvariant())) && calls == 1,
                "A duplicate folder started another scan.");
            Check(!Finish(view.AddLibraryDirectoryAsync(string.Empty)), "An empty picker result was admitted.");
            Check(Finish(view.AddLibraryDirectoryAsync(second)) && calls == 2 && changed == 1,
                "A second new directory did not scan and invalidate the catalog.");
            Check(ReferenceEquals(panel.Children[0], firstRow) && Equals(scan.Content, LocalizationManager.Current.Cancel),
                "Adding another directory replaced an active scan row.");
            var secondRow = (StackPanel)panel.Children[1];
            ((Button)((Grid)secondRow.Children[0]).Children[4]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(panel.Children.Single(), firstRow), "Removing another folder replaced active controls.");
            ((Button)firstGrid.Children[4]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(firstToken.IsCancellationRequested && panel.Children.Count == 0,
                "Removing an automatically scanned folder did not cancel its owner.");
            Check(!Finish(view.AddLibraryDirectoryAsync(first.ToUpperInvariant())),
                "A removed folder was readmitted before its cancelled scan completed.");
            gate.TrySetCanceled(firstToken);
            Check(Finish(addFirst), "Cancellation lost the admitted folder outcome.");
            Check(view.FindControl<Button>("ExportLibraryButton")!.IsEnabled,
                "Cancellation retained the backup operation lock.");

            view.DirectoryScanner = (_, _, _, _) => Task.FromException<ScanResult>(new IOException("Synthetic scan failure"));
            Check(Finish(view.AddLibraryDirectoryAsync(first)), "Failed scan lost the configured directory.");
            var failedRow = (StackPanel)panel.Children.Single();
            var failedGrid = (Grid)failedRow.Children[0];
            Check(((TextBlock)failedRow.Children[1]).Text?.Contains("Synthetic scan failure") == true &&
                Equals(((Button)failedGrid.Children[2]).Content, LocalizationManager.Current.Scan) &&
                ((Button)failedGrid.Children[3]).IsEnabled,
                "Failed automatic scan did not restore its retry controls.");
            var retried = false;
            view.DirectoryScanner = (_, _, _, _) =>
            {
                retried = true;
                return Task.FromResult(new ScanResult(1, 1, 0, 0, 0));
            };
            ((Button)failedGrid.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(retried && changed == 2, "Manual Scan did not retry through the shared scanner.");
            view.Deactivate();
            Check(!Finish(view.AddLibraryDirectoryAsync(second)), "Closed Settings admitted a late picker result.");
            Console.WriteLine("PASS: new directory auto-scan shares progress, cancellation, failure and row ownership");
        }
        finally
        {
            gate.TrySetCanceled();
            view.Deactivate();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Pumps owning-thread continuations with a diagnostic timeout.</summary>
    /// <param name="task">Production adapter task.</param>
    /// <returns>The directory admission result.</returns>
    private static bool Finish(Task<bool> task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Check(task.IsCompleted, "Directory setup workflow timed out.");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Throws a fixed diagnostic without personal source data.</summary>
    /// <param name="condition">Expected invariant.</param>
    /// <param name="message">Fixed diagnostic.</param>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
