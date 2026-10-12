using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Orynivo.Localization;
using Orynivo.Streaming;

namespace Orynivo.Ui.Smoke;

/// <summary>Checks the compiled source setup controls without startup or audio hardware.</summary>
internal static class EmptyLibraryWorkflows
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Checks empty/source visibility, localized layout, focus and real Settings routing.</summary>
    /// <param name="root">Process-owned synthetic library root.</param>
    internal static void Run(string root)
    {
        var window = new MainWindow();
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", Private)!.GetValue(window)!;
        var hint = window.FindControl<ListBoxItem>("LibraryEmptyHintItem")!;
        var folders = window.FindControl<Button>("LibrarySetupButton")!;
        var servers = window.FindControl<Button>("LibraryServerSetupButton")!;
        var output = window.FindControl<Button>("LibraryOutputSetupButton")!;
        settings.LibraryPaths.Clear();
        settings.OrynivoServers.Clear();
        settings.ShowLocalLibrarySection = true;
        settings.IsLocalLibrarySectionExpanded = true;
        ApplyVisibility(window);
        Check(hint.IsVisible && double.IsPositiveInfinity(hint.MaxHeight),
            "Empty-library setup is hidden or height capped.");
        settings.LibraryPaths.Add(root);
        ApplyVisibility(window);
        Check(hint.MaxHeight == 0, "Local source did not collapse setup.");
        settings.LibraryPaths.Clear();
        settings.OrynivoServers.Add(new OrynivoServerSettings { Id = "setup", Name = "Synthetic source" });
        ApplyVisibility(window);
        Check(hint.MaxHeight == 0 && window.FindControl<ListBoxItem>("TracksNavItem")!.IsVisible,
            "Server-only configuration did not expose the unified library.");
        settings.OrynivoServers.Clear();
        ApplyVisibility(window);
        Console.WriteLine("PASS: empty-library setup yields to local and server-only sources");

        var sidebar = window.FindControl<ListBox>("NavListBox")!;
        var index = sidebar.Items.IndexOf(hint);
        sidebar.Items.Remove(hint);
        var host = new Window { Width = 230, Height = 700, Content = hint };
        try
        {
            host.Show();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            foreach (var language in Enum.GetValues<Language>())
            foreach (var width in new[] { 180d, 230d })
            {
                ThemeManager.Apply(theme);
                LocalizationManager.Apply(language);
                host.Width = width;
                Dispatcher.UIThread.RunJobs();
                hint.Measure(new Size(width, 700));
                hint.Arrange(new Rect(0, 0, width, hint.DesiredSize.Height));
                Dispatcher.UIThread.RunJobs();
                foreach (var button in new[] { folders, servers, output })
                {
                    var text = (TextBlock)button.Content!;
                    Check(!string.IsNullOrWhiteSpace(text.Text) && text.Bounds.Height > 0 &&
                        text.Bounds.Width <= button.Bounds.Width && button.Bounds.Height > 0 &&
                        text.Bounds.Height + 1 >= text.DesiredSize.Height,
                        "Localized setup action lacks bounded wrapped content.");
                    var point = button.TranslatePoint(default, hint);
                    Check(point.HasValue && point.Value.X >= 0 && point.Value.Y >= 0 &&
                        point.Value.X + button.Bounds.Width <= hint.Bounds.Width + 1 &&
                        point.Value.Y + button.Bounds.Height <= hint.Bounds.Height + 1,
                        "Setup action is clipped by the compiled hint row.");
                    Check(button.Focus() && ReferenceEquals(host.FocusManager!.GetFocusedElement(), button),
                        "Setup action cannot receive keyboard focus.");
                    Check(ControlAutomationPeer.CreatePeerForElement(button)!.GetName() == text.Text,
                        "Setup action lacks its localized accessible name.");
                }
                var hintText = ((StackPanel)hint.Content!).Children.OfType<TextBlock>().Single();
                Check(hintText.Text == LocalizationManager.Current.LibraryEmptyHint,
                    "Setup hint failed runtime language refresh.");
                Check(((TextBlock)folders.Content!).Text == LocalizationManager.Current.Directories &&
                    ((TextBlock)servers.Content!).Text == LocalizationManager.Current.OrynivoServersSettings &&
                    ((TextBlock)output.Content!).Text == LocalizationManager.Current.OutputDevice,
                    "Setup actions failed runtime language refresh.");
            }
            Console.WriteLine("PASS: compiled setup actions wrap and focus in seven languages, two themes and two widths");
        }
        finally
        {
            host.Content = null;
            host.Close();
            sidebar.Items.Insert(index, hint);
            LocalizationManager.Apply(Language.English);
            ThemeManager.Apply(AppTheme.Dark);
        }

        CheckSidebarKeyboard(window, sidebar, hint, folders, servers, output);
        try
        {
            foreach (var entry in new[] { (folders, "LibraryPanel"), (servers, "OrynivoServersSettingsPanel"),
                         (output, "AudioDevicePanel") })
            {
                entry.Item1.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var settingsHost = window.FindControl<ContentControl>("SettingsViewHost")!;
                Check(settingsHost.IsVisible && settingsHost.Content is SettingsView,
                    "Setup action did not open shared embedded Settings.");
                var view = (SettingsView)settingsHost.Content!;
                Check(view.FindControl<Control>(entry.Item2)!.IsVisible,
                    "Setup action opened the wrong Settings section.");
                typeof(MainWindow).GetMethod("CloseEmbeddedSettings", Private)!.Invoke(window, null);
            }
            Console.WriteLine("PASS: folder, server and output setup actions open their existing Settings sections");
            var originalProfiles = settings.OutputProfiles;
            var originalSelection = settings.SelectedOutputProfileName;
            try
            {
                settings.OutputProfiles = [];
                settings.SelectedOutputProfileName = null;
                output.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var view = (SettingsView)window.FindControl<ContentControl>("SettingsViewHost")!.Content!;
                Check(view.FindControl<Control>("AudioDevicePanel")!.IsVisible &&
                    view.FindControl<ComboBox>("OutputProfileComboBox")!.SelectedItem is null &&
                    view.FindControl<Button>("CreateOutputProfileButton")!.IsEnabled &&
                    !view.FindControl<Button>("ConfigureOutputProfileButton")!.IsEnabled &&
                    !view.FindControl<Button>("DeleteOutputProfileButton")!.IsEnabled,
                    "Empty output setup does not expose a usable create action.");
                Console.WriteLine("PASS: output setup without profiles permits creation and disables selection-only actions");
                CheckOutputLayout(view);
            }
            finally
            {
                settings.OutputProfiles = originalProfiles;
                settings.SelectedOutputProfileName = originalSelection;
            }
        }
        finally
        {
            typeof(MainWindow).GetMethod("CloseEmbeddedSettings", Private)!.Invoke(window, null);
            Dispatcher.UIThread.RunJobs();
        }
        CheckFirstLocalSource(window, settings, root);
        CheckEmptyViewNotice(window, settings, root);
        CheckEmptyViewLoadOwnership(window, settings);
    }

    /// <summary>Checks headless key dispatch through the compiled sidebar with only the setup item admitted.</summary>
    /// <param name="window">The unshown production window that owns Settings routing.</param>
    /// <param name="sidebar">The compiled navigation ListBox.</param>
    /// <param name="hint">Its non-leaf setup item.</param>
    /// <param name="folders">The local setup action.</param>
    /// <param name="servers">The server setup action.</param>
    /// <param name="output">The output setup action.</param>
    private static void CheckSidebarKeyboard(MainWindow window, ListBox sidebar, ListBoxItem hint,
        Button folders, Button servers, Button output)
    {
        var parent = (Panel)sidebar.Parent!;
        var index = parent.Children.IndexOf(sidebar);
        var items = sidebar.Items.Cast<object>().ToArray();
        var originalHeight = sidebar.Height;
        parent.Children.Remove(sidebar);
        sidebar.Items.Clear(); sidebar.Items.Add(hint); sidebar.Height = 400;
        var before = new Button { Content = "Synthetic before" };
        var after = new Button { Content = "Synthetic after" };
        var stack = new StackPanel(); stack.Children.Add(before); stack.Children.Add(sidebar); stack.Children.Add(after);
        var host = new Window { Width = 270, Height = 600, Content = stack };
        void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var physical = key == Key.Tab ? PhysicalKey.Tab : PhysicalKey.Enter;
            host.KeyPress(key, modifiers, physical, null); host.KeyRelease(key, modifiers, physical, null);
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            host.Show(); Dispatcher.UIThread.RunJobs(); before.Focus();
            for (var step = 0; step < 4 && !ReferenceEquals(host.FocusManager!.GetFocusedElement(), folders); step++)
                Press(Key.Tab);
            Check(ReferenceEquals(host.FocusManager!.GetFocusedElement(), folders), "Tab did not reach sidebar folder setup.");
            Press(Key.Tab); Check(ReferenceEquals(host.FocusManager.GetFocusedElement(), servers), "Tab skipped server setup.");
            Press(Key.Tab); Check(ReferenceEquals(host.FocusManager.GetFocusedElement(), output), "Tab skipped output setup.");
            Press(Key.Tab); Check(ReferenceEquals(host.FocusManager.GetFocusedElement(), after), "Sidebar trapped forward Tab.");
            Press(Key.Tab, RawInputModifiers.Shift);
            Check(ReferenceEquals(host.FocusManager.GetFocusedElement(), output), "Reverse Tab did not enter output setup.");
            Press(Key.Tab, RawInputModifiers.Shift);
            Check(ReferenceEquals(host.FocusManager.GetFocusedElement(), servers), "Reverse Tab skipped server setup.");
            Press(Key.Tab, RawInputModifiers.Shift);
            Check(ReferenceEquals(host.FocusManager.GetFocusedElement(), folders), "Reverse Tab skipped folder setup.");
            Press(Key.Enter);
            var settingsHost = window.FindControl<ContentControl>("SettingsViewHost")!;
            Check(settingsHost.IsVisible && settingsHost.Content is SettingsView view &&
                view.FindControl<Control>("LibraryPanel")!.IsVisible, "Enter did not activate actual folder Settings routing.");
            typeof(MainWindow).GetMethod("CloseEmbeddedSettings", Private)!.Invoke(window, null);
            Console.WriteLine("PASS: sidebar setup supports headless Tab, reverse Tab and Enter activation");
        }
        finally
        {
            typeof(MainWindow).GetMethod("CloseEmbeddedSettings", Private)!.Invoke(window, null);
            host.Content = null; host.Close(); stack.Children.Remove(sidebar);
            sidebar.Items.Clear(); foreach (var item in items) sidebar.Items.Add(item);
            sidebar.Height = originalHeight; parent.Children.Insert(index, sidebar);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Checks real catalog publication, cache, supersession and watcher freshness before seeding media.</summary>
    /// <param name="window">The unshown synthetic window.</param>
    /// <param name="settings">Its isolated source configuration.</param>
    private static void CheckEmptyViewLoadOwnership(MainWindow window, AppSettings settings)
    {
        object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, values);
        var client = typeof(MainWindow).GetField("_orynivoClient", Private)!.GetValue(window)!;
        var transportField = typeof(OrynivoServerClient).GetField("_http", Private)!;
        var originalTransport = transportField.GetValue(client);
        var originalServers = settings.OrynivoServers;
        var handler = new CatalogRecoveryHandler();
        using var transport = new HttpClient(handler);
        transportField.SetValue(client, transport);
        settings.OrynivoServers = [new() { Id = "empty-owned", Name = "Synthetic source", BaseUrl = "https://empty.invalid" }];
        var notice = window.FindControl<Border>("LibraryEmptyNotice")!;
        Task Bind() => (Task)Call("BindLocalRowsAndStartRemoteAppendAsync", "Tracks", false)!;
        void PumpUntil(Func<bool> completed)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!completed() && timer.Elapsed < TimeSpan.FromSeconds(15))
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Dispatcher.UIThread.RunJobs(); Check(completed(), "Empty-view ownership workflow timed out.");
        }
        void Finish(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        try
        {
            Finish(Bind()); Check(notice.IsVisible, "Current complete empty load did not show guidance.");
            Finish(Bind()); Check(notice.IsVisible, "Complete empty cache hit lost current guidance.");
            Call("InvalidateUnifiedLibraryViewCache");
            Check(!notice.IsVisible, "Invalidated catalog retained empty guidance.");
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Pending = pending;
            var held = Bind(); PumpUntil(() => handler.PendingCount > 0);
            Call("UpdateUnifiedLibraryLoadNotice");
            Check(!held.IsCompleted && !notice.IsVisible, "Held reload revived obsolete empty guidance.");
            handler.Pending = null; handler.Fail = true;
            Finish(Bind());
            pending.SetResult(); Finish(held);
            Check(!notice.IsVisible && window.FindControl<Border>("LibraryLoadNotice")!.IsVisible,
                "Late empty completion replaced the current incomplete outcome.");
            handler.Fail = false; Call("InvalidateUnifiedLibraryViewCache"); Finish(Bind());
            Check(notice.IsVisible, "Fresh successful recovery did not show guidance.");
            Task.Run(() => Call("OnWatchedLibraryChanged")).GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
            Check(!notice.IsVisible, "Watcher invalidation retained obsolete empty guidance.");
            Finish(Bind()); Check(notice.IsVisible, "Reload after watcher did not restore current guidance.");
            Console.WriteLine("PASS: empty guidance retains current catalog ownership through reload, cache and watcher recovery");
        }
        finally
        {
            handler.ReleaseGates(); Call("ResetDrilldownState", true);
            settings.OrynivoServers = originalServers;
            transportField.SetValue(client, originalTransport);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Checks original notice publication and Settings routing without loading real sources.</summary>
    /// <param name="window">The unshown synthetic window.</param>
    /// <param name="settings">Its isolated configuration.</param>
    /// <param name="root">Configured synthetic local root.</param>
    private static void CheckEmptyViewNotice(MainWindow window, AppSettings settings, string root)
    {
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        void Update() => typeof(MainWindow).GetMethod("UpdateUnifiedLibraryLoadNotice", Private)!.Invoke(window, null);
        var notice = window.FindControl<Border>("LibraryEmptyNotice")!;
        var search = window.FindControl<ScrollViewer>("SearchResultsScrollViewer")!;
        search.IsVisible = false;
        foreach (var name in new[] { "LyricsView", "ArtistInfoView", "PodcastInfoView" })
            window.FindControl<Control>(name)!.IsVisible = false;
        var success = new LibraryLoadResult<ContentRow>([], [new("local", LibrarySourceLoadStatus.Success)]);
        var owner = (LibraryCatalogLoadCoordinator<ContentRow>)typeof(MainWindow)
            .GetField("_unifiedLibraryLoads", Private)!.GetValue(window)!;
        Set("_unifiedLibraryPublishedRequest", owner.Begin("synthetic-empty-view", CancellationToken.None));
        foreach (var tag in new[] { "Artists", "Albums", "Tracks" })
        {
            Set("_currentTopLevelTag", tag); Set("_unifiedLibraryLoadResult", success); Update();
            Check(notice.IsVisible && !window.FindControl<Border>("LibraryLoadNotice")!.IsVisible,
                "Successful empty catalog did not show separate setup guidance.");
        }
        foreach (var status in new[] { LibrarySourceLoadStatus.Failed, LibrarySourceLoadStatus.TimedOut,
                     LibrarySourceLoadStatus.Cancelled })
        {
            Set("_unifiedLibraryLoadResult", new LibraryLoadResult<ContentRow>([], [new("local", status)]));
            Update(); Check(!notice.IsVisible, "Unavailable catalog was presented as empty.");
        }
        Set("_unifiedLibraryLoadResult", new LibraryLoadResult<ContentRow>([],
            [new("local", LibrarySourceLoadStatus.Success), new("remote", LibrarySourceLoadStatus.Failed)]));
        Update(); Check(!notice.IsVisible, "Partial catalog was presented as empty.");
        Set("_unifiedLibraryLoadResult", new LibraryLoadResult<ContentRow>([], []));
        Update(); Check(!notice.IsVisible, "A source-free selection was presented as an empty catalog.");
        Set("_unifiedLibraryLoadResult", new LibraryLoadResult<ContentRow>([new() { Title = "Synthetic row" }], success.Sources));
        Update(); Check(!notice.IsVisible, "Populated catalog retained empty guidance.");
        Set("_unifiedLibraryLoadResult", success); search.IsVisible = true; Update();
        Check(!notice.IsVisible, "Library guidance leaked into search."); search.IsVisible = false;
        Set("_activePlaylistId", 1L); Update();
        Check(!notice.IsVisible, "Library guidance leaked into a playlist."); Set("_activePlaylistId", null);
        window.FindControl<Control>("ArtistInfoView")!.IsVisible = true; Update();
        Check(!notice.IsVisible, "Library guidance leaked into artist detail.");
        window.FindControl<Control>("ArtistInfoView")!.IsVisible = false;
        foreach (var language in Enum.GetValues<Language>())
        {
            LocalizationManager.Apply(language); Update();
            Check(notice.IsVisible && window.FindControl<TextBlock>("LibraryEmptyNoticeMessage")!.Text ==
                LocalizationManager.Current.LibraryViewEmptyHint, "Empty guidance failed language refresh.");
        }
        LocalizationManager.Apply(Language.English);
        var button = window.FindControl<Button>("LibraryEmptySettingsButton")!;
        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var host = window.FindControl<ContentControl>("SettingsViewHost")!;
            var view = (SettingsView)host.Content!;
            Check(view.FindControl<Control>("LibraryPanel")!.IsVisible, "Local empty setup opened the wrong Settings section.");
            settings.LibraryPaths = [];
            settings.OrynivoServers = [new OrynivoServerSettings { Id = "empty", Name = "Synthetic source" }];
            // Reuse the already open synthetic view so no server probe is constructed.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.FindControl<Control>("OrynivoServersSettingsPanel")!.IsVisible,
                "Server-only empty setup opened local folders.");
            settings.OrynivoServers.Clear(); Update();
            Check(!notice.IsVisible, "Unconfigured source retained catalog guidance.");
        }
        finally
        {
            settings.LibraryPaths = [root]; settings.OrynivoServers.Clear();
            typeof(MainWindow).GetMethod("CloseEmbeddedSettings", Private)!.Invoke(window, null);
            typeof(MainWindow).GetMethod("ClearUnifiedLibraryLoadNotice", Private)!.Invoke(window, null);
        }
        Check(!notice.IsVisible, "Abandoned navigation retained empty guidance.");
        Console.WriteLine("PASS: empty-view guidance distinguishes catalog outcomes and routes local/server Settings");
    }

    /// <summary>Checks the shared production root setter without scanning or removing catalog rows.</summary>
    /// <param name="window">The unshown synthetic application window.</param>
    /// <param name="settings">Its process-isolated configuration.</param>
    /// <param name="root">Process-owned synthetic root.</param>
    private static void CheckFirstLocalSource(MainWindow window, AppSettings settings, string root)
    {
        settings.LibraryPaths = [];
        settings.IsLocalLibrarySectionExpanded = false;
        var roots = new List<string> { root };
        typeof(MainWindow).GetMethod("SetConfiguredLibraryPaths", Private)!.Invoke(window, [roots]);
        roots.Clear();
        ApplyVisibility(window);
        Check(settings.IsLocalLibrarySectionExpanded && settings.LibraryPaths.SequenceEqual(new[] { root }) &&
            window.FindControl<ListBoxItem>("TracksNavItem")!.IsVisible &&
            window.FindControl<ListBoxItem>("AlbumsNavItem")!.IsVisible &&
            window.FindControl<ListBoxItem>("ArtistsNavItem")!.IsVisible &&
            window.FindControl<ListBoxItem>("LibraryEmptyHintItem")!.MaxHeight == 0,
            "First local source did not reveal the library with copied roots.");
        settings.IsLocalLibrarySectionExpanded = false;
        typeof(MainWindow).GetMethod("SetConfiguredLibraryPaths", Private)!.Invoke(window,
            [new List<string> { root, Path.Combine(root, "second") }]);
        ApplyVisibility(window);
        Check(!settings.IsLocalLibrarySectionExpanded &&
            window.FindControl<ListBoxItem>("TracksNavItem")!.MaxHeight == 0,
            "Later root changes discarded the collapsed-library preference.");
        Console.WriteLine("PASS: first local source reveals library while later root changes retain collapsed preference");
    }

    /// <summary>Checks the compiled output selector and wrapping actions in a bounded host.</summary>
    /// <param name="view">The existing synthetic Settings view with no selected output.</param>
    private static void CheckOutputLayout(SettingsView view)
    {
        var panel = view.FindControl<StackPanel>("OutputProfileSetupPanel")!;
        var parent = (StackPanel)panel.Parent!;
        var index = parent.Children.IndexOf(panel);
        parent.Children.Remove(panel);
        var host = new Window { Width = 240, Height = 500, Content = panel };
        try
        {
            host.Show();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            foreach (var language in Enum.GetValues<Language>())
            foreach (var width in new[] { 240d, 640d })
            {
                ThemeManager.Apply(theme);
                LocalizationManager.Apply(language);
                host.Width = width;
                Dispatcher.UIThread.RunJobs();
                panel.Measure(new Size(width, 500));
                panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
                Dispatcher.UIThread.RunJobs();
                var selector = view.FindControl<ComboBox>("OutputProfileComboBox")!;
                Check(selector.Bounds.Width >= width - 1 && selector.Bounds.Height > 0,
                    "Output selector lost its usable width.");
                foreach (var name in new[] { "ConfigureOutputProfileButton", "CreateOutputProfileButton",
                             "DeleteOutputProfileButton" })
                {
                    var button = view.FindControl<Button>(name)!;
                    var text = (TextBlock)button.Content!;
                    var point = button.TranslatePoint(default, panel);
                    Check(point.HasValue && point.Value.X >= 0 && point.Value.Y >= 0 &&
                        point.Value.X + button.Bounds.Width <= width + 1 &&
                        point.Value.Y + button.Bounds.Height <= panel.Bounds.Height + 1 &&
                        text.Bounds.Height > 0 && text.Bounds.Height + 1 >= text.DesiredSize.Height,
                        "Output action or translated caption is clipped.");
                    Check(ControlAutomationPeer.CreatePeerForElement(button)!.GetName() == text.Text,
                        "Output action lacks its localized accessible name.");
                }
            }
            Console.WriteLine("PASS: output selector and translated actions fit narrow and wide Settings layouts");
        }
        finally
        {
            host.Content = null;
            host.Close();
            parent.Children.Insert(index, panel);
            LocalizationManager.Apply(Language.English);
            ThemeManager.Apply(AppTheme.Dark);
        }
    }

    /// <summary>Invokes the original library visibility adapter.</summary>
    /// <param name="window">Unshown synthetic application window.</param>
    private static void ApplyVisibility(MainWindow window) =>
        typeof(MainWindow).GetMethod("ApplyLibrarySectionVisibility", Private)!.Invoke(window, null);

    /// <summary>Throws a fixed diagnostic without source or device details.</summary>
    /// <param name="condition">Expected invariant.</param>
    /// <param name="message">Fixed diagnostic.</param>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
