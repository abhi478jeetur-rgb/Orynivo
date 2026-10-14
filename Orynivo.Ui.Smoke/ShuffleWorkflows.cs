using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Orynivo.Audio;
using Orynivo.Library;

namespace Orynivo.Ui.Smoke;

/// <summary>Checks real shuffle controls and queue navigation without audio preparation or devices.</summary>
internal static class ShuffleWorkflows
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Exercises mode refresh, duplicate exclusion, exhaustion and replay history on synthetic queues.</summary>
    internal static void Run()
    {
        var window = new MainWindow();
        object Get(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        object? Call(string name) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null);
        var refreshes = 0;
        Set("_gaplessQueueRefresh", new GaplessQueueRefreshCoordinator(
            () => { refreshes++; return false; }, () => null,
            () => throw new InvalidOperationException("Unexpected audio preparation."),
            _ => true, () => true));
        var button = window.FindControl<Button>("ShuffleButton")!;
        foreach (var tag in new[] { "GenreCloud", "Tracks", "Albums", "Favorites", "Queue", "Playlist:synthetic", "Plex:synthetic" })
        {
            Set("_currentTopLevelTag", tag);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check((bool)Get("_shuffleEnabled"), "Shuffle control did not enable the mode.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!(bool)Get("_shuffleEnabled"), "Shuffle control did not disable the mode.");
        }
        Check(refreshes == 14, "Shuffle control omitted the prepared-output refresh.");
        var queue = (IList<PlaylistItem>)Get("_queue");
        queue.Clear();
        foreach (var path in new[] { "synthetic-a.flac", "synthetic-a.flac", "synthetic-b.flac", "synthetic-c.flac" })
            queue.Add(new PlaylistItem(path));
        Set("_queueIndex", 0);
        Call("ResetQueuePlaybackState");
        var build = typeof(MainWindow).GetMethod("BuildGaplessPlaybackItems", Private)!;
        var sequential = (IReadOnlyList<GaplessPlaybackItem>)build.Invoke(window, [queue[0].FilePath, true])!;
        Check(sequential.Count == 4, "Sequential preparation lost queued occurrences.");
        var played = (HashSet<string>)Get("_playedQueuePaths");
        played.Add(queue[0].FilePath);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var shuffled = (IReadOnlyList<GaplessPlaybackItem>)build.Invoke(window, [queue[0].FilePath, true])!;
        Check(shuffled.Count == 1 && shuffled[0].FilePath == queue[0].FilePath,
            "Shuffle preparation retained sequential successors.");
        var visited = new HashSet<string> { queue[0].FilePath };
        while ((bool)Call("TryMoveToNextQueueIndex")!)
        {
            var path = queue[(int)Get("_queueIndex")].FilePath;
            Check(visited.Add(path), "Shuffle repeated an already played or duplicate path.");
            played.Add(path);
        }
        Check(visited.Count == 3 && !(bool)Call("HasNextQueueItem")!, "Shuffle did not exhaust distinct tracks.");
        Check((bool)Call("TryMoveToPreviousQueueIndex")!, "Shuffle history lost Previous.");
        Check((bool)Call("HasNextQueueItem")!, "Automatic advancement lost forward shuffle history.");
        Check((bool)Call("TryMoveToNextQueueIndex")!, "Shuffle history lost Next after Previous.");
        Check(!(bool)Call("TryMoveToNextQueueIndex")!, "Shuffle restarted an exhausted queue.");
        Console.WriteLine("PASS: shuffle refreshes output across views and preserves distinct-track history");
    }

    /// <summary>Rejects a failed synthetic workflow invariant.</summary>
    /// <param name="condition">Whether the invariant holds.</param>
    /// <param name="message">Fixed diagnostic text.</param>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
