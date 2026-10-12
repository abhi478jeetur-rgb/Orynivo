using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
