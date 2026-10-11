using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using AvaloniaEllipse = Avalonia.Controls.Shapes.Ellipse;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using Orynivo.Audio;
using Orynivo.Controls;
using Orynivo.Library;
using Orynivo.Localization;
using Orynivo.Streaming;
using Windows.Media;

namespace Orynivo;

/// <summary>
/// Plex Media Server library browsing, paging, drill-down, and lazy folder tree
/// for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly NavigationLoadOwnership _plexLoads = new();

    /// <summary>Captures transient Plex request inputs and publication ownership.</summary>
    /// <param name="Request">Foreground cancellation and request identity.</param>
    /// <param name="Server">Copied connection settings, never persisted by this context.</param>
    /// <param name="Token">Transient authentication token.</param>
    /// <param name="Section">Library section identity.</param>
    /// <param name="View">Admitted view mode.</param>
    /// <param name="NavigationTag">Admitted sidebar identity.</param>
    /// <param name="Restore">Optional enclosing Back request.</param>
    /// <param name="ParentIsCurrent">Optional enclosing root load ownership.</param>
    private sealed record PlexLoadContext(NavigationLoadOwnership.Request Request,
        PlexServerSettings Server, string? Token, string? Section, string View,
        string NavigationTag, NavigationLoadOwnership.Request? Restore, Func<bool>? ParentIsCurrent);

    /// <summary>Abandons Plex page, child and lazy-folder publication.</summary>
    private void CancelPlexViewLoad()
    {
        _plexLoads.Invalidate();
        CancelAndDispose(ref _plexViewCts);
    }

    /// <summary>Admits copied Plex inputs before cancelling older requests.</summary>
    /// <param name="isParentCurrent">Optional enclosing root load ownership.</param>
    /// <param name="allowSectionless">Whether a source-owned history detail may omit a library section.</param>
    /// <returns>Admitted inputs, or null when the current view cannot load Plex.</returns>
    private PlexLoadContext? BeginPlexLoad(Func<bool>? isParentCurrent, bool allowSectionless = false)
    {
        if (_activePlexServer is null || _currentTopLevelTag is null ||
            !_navigationRestores.TryAdmitNavigation()) return null;
        var isLibrary = !string.IsNullOrWhiteSpace(_activePlexSectionKey) &&
            _currentTopLevelTag == $"PlexLibrary:{_activePlexServer.Id}:{_activePlexSectionKey}";
        if (!isLibrary && !(allowSectionless && _activePlexSectionKey is null)) return null;
        var previousCancellation = _plexViewCts;
        _plexViewCts = new CancellationTokenSource();
        var context = new PlexLoadContext(_plexLoads.Begin(_plexViewCts.Token),
            new PlexServerSettings { Id = _activePlexServer.Id, Name = _activePlexServer.Name,
                BaseUrl = _activePlexServer.BaseUrl },
            _activePlexToken, _activePlexSectionKey, _activePlexView, _currentTopLevelTag,
            _navigationRestores.Context, isParentCurrent);
        if (isParentCurrent is null)
        {
            _topLevelViewLoads.Invalidate();
            var previousTopLevelCancellation = _topLevelViewCts;
            _topLevelViewCts = null;
            CancelAndDispose(ref previousTopLevelCancellation);
        }
        CancelAndDispose(ref previousCancellation);
        return IsCurrentPlexLoad(context) ? context : null;
    }

    /// <summary>Checks Plex request, source, section, navigation and enclosing ownership.</summary>
    /// <param name="context">Captured transient request inputs.</param>
    /// <returns>Whether the request may publish into the current window.</returns>
    private bool IsCurrentPlexLoad(PlexLoadContext context) =>
        _plexLoads.IsCurrent(context.Request) && _currentTopLevelTag == context.NavigationTag &&
        _activePlexSectionKey == context.Section && _activePlexToken == context.Token &&
        _activePlexServer?.Id == context.Server.Id &&
        _activePlexServer.BaseUrl == context.Server.BaseUrl && _activePlexServer.Name == context.Server.Name &&
        (_settings.PlexServers ?? []).Any(server => server.Id == context.Server.Id &&
            server.BaseUrl == context.Server.BaseUrl && server.Name == context.Server.Name) &&
        (context.Restore is null || _navigationRestores.IsCurrent(context.Restore)) &&
        (context.ParentIsCurrent is null || context.ParentIsCurrent());

    /// <summary>Initializes one configured Plex library under its enclosing root request.</summary>
    /// <param name="tag">Configured library sidebar identity.</param>
    /// <param name="isParentCurrent">Enclosing root load publication guard.</param>
    /// <returns>A task completing after initial page loading or abandonment.</returns>
    private async Task ShowPlexLibraryAsync(string tag, Func<bool> isParentCurrent)
    {
        var parts = tag.Split(':', 3);
        if (parts.Length != 3)
            return;

        _activePlexServer = (_settings.PlexServers ?? [])
            .FirstOrDefault(server => string.Equals(server.Id, parts[1], StringComparison.Ordinal));
        if (_activePlexServer is null)
            return;

        _activePlexSectionKey = parts[2];
        _activePlexSectionTitle = NavListBox.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            ?.Content is TextBlock text
                ? (text.Text ?? string.Empty).Trim()
                : _activePlexServer.Name;
        try
        {
            _activePlexToken = new WindowsPlexCredentialStore()
                .LoadAll()
                .GetValueOrDefault(_activePlexServer.Id);
        }
        catch
        {
            _activePlexToken = null;
        }

        _activePlexView = "Artists";
        _plexNavigationStack.Clear();
        _plexDetailVisible = false;
        _updatingViewMode = true;
        PlexArtistsViewRadioButton.IsChecked = true;
        _updatingViewMode = false;
        ContentTitleTextBlock.Text = _activePlexSectionTitle;
        await LoadPlexViewAsync(reset: true, isParentCurrent);
    }

    /// <summary>Leaves published Plex detail for an independently owned library mode load.</summary>
    /// <param name="sender">Checked Plex mode radio button.</param>
    /// <param name="e">Mode selection event.</param>
    private async void PlexViewModeRadioButton_OnChecked(object? sender, RoutedEventArgs e)
    {
        if (!IsVisible || _updatingViewMode ||
            sender is not RadioButton { IsChecked: true, Tag: string view } ||
            _activePlexServer is null)
        {
            return;
        }

        _plexNavigationStack.Clear();
        _plexDetailVisible = false;
        _activePlexView = view;
        ContentTitleTextBlock.Text = _activePlexSectionTitle;
        BackButton.IsVisible = _navigationStack.Count > 0;
        await LoadPlexViewAsync(reset: true);
    }

    private async void PlexLoadMoreButton_OnClick(object? sender, RoutedEventArgs e)
        => await LoadPlexViewAsync(reset: false);

    /// <summary>Loads a Plex page from captured inputs and rejects abandoned publication.</summary>
    /// <param name="reset">Whether to replace the page instead of appending.</param>
    /// <param name="isParentCurrent">Optional enclosing root request guard.</param>
    /// <returns>A task completing after page publication or abandonment.</returns>
    private async Task LoadPlexViewAsync(bool reset, Func<bool>? isParentCurrent = null)
    {
        var context = BeginPlexLoad(isParentCurrent);
        if (context is null) return;
        bool CanPublish() => IsCurrentPlexLoad(context) && _activePlexView == context.View;
        var cancellationToken = context.Request.Token;
        var server = context.Server;
        var token = context.Token;
        var sectionKey = context.Section!;
        var view = context.View;
        var start = reset ? 0 : _plexLoadedCount;
        var previousRows = reset ? [] : ((ContentDataGrid.ItemsSource as IEnumerable<ContentRow>) ?? []).ToList();
        if (reset)
        {
            _plexLoadedCount = 0;
            _plexTotalCount = 0;
        }

        ContentDataGrid.IsVisible = view != "Folders";
        FolderTreeView.IsVisible = view == "Folders";
        AlbumArtworkListBox.IsVisible = false;
        ArtistArtworkListBox.IsVisible = false;
        SearchResultsScrollViewer.IsVisible = false;
        PlexLoadMoreButton.IsVisible = false;
        StatusTextBlock.Text = LocalizationManager.Current.PlexLoading;
        if (view == "Folders")
            UpdateAlphabetIndex(null, false);

        try
        {
            if (view == "Folders")
            {
                await BuildPlexFolderTreeAsync(
                    context);
                if (!CanPublish())
                {
                    return;
                }
                UpdatePlexFolderAlphabetIndex();
                ContentCountTextBlock.Text = string.Empty;
                StatusTextBlock.Text = string.Empty;
                return;
            }

            var mediaType = view switch
            {
                "Artists" => 8,
                "Albums" => 9,
                _ => 10
            };
            var page = await _plexClient.GetLibraryItemsAsync(
                server,
                token,
                sectionKey,
                mediaType,
                start,
                PlexPageSize,
                cancellationToken);
            if (!CanPublish())
            {
                return;
            }

            var newRows = page.Items
                .Select(item => ToPlexContentRow(item, view, server, token))
                .ToList();
            var rows = reset
                ? newRows
                : previousRows.Concat(newRows)
                    .ToList();
            _plexLoadedCount = rows.Count;
            _plexTotalCount = page.TotalSize;
            ApplyColumns("Plex" + view);
            ContentDataGrid.ItemsSource = rows;
            UpdateAlphabetIndex(rows, view is "Artists" or "Albums" or "Tracks");
            ContentCountTextBlock.Text = $"{_plexLoadedCount:N0} / {_plexTotalCount:N0}";
            PlexLoadMoreButton.IsVisible = _plexLoadedCount < _plexTotalCount;
            StatusTextBlock.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!CanPublish()) return;
            StatusTextBlock.Text = string.Format(
                LocalizationManager.Current.PlexConnectionFailed,
                ex.Message);
        }
    }

    /// <summary>Maps an admitted Plex item and registers playable track metadata.</summary>
    /// <param name="item">Plex metadata to map.</param>
    /// <param name="view">Entity view mode.</param>
    /// <param name="server">Captured owning connection.</param>
    /// <param name="token">Captured transient authentication token.</param>
    /// <returns>A source-aware playable or navigable row.</returns>
    private ContentRow ToPlexContentRow(
        PlexMediaItem item,
        string view,
        PlexServerSettings server,
        string? token)
    {
        var entityType = view switch
        {
            "Artists" => "PlexArtist",
            "Albums" => "PlexAlbum",
            _ => "PlexTrack"
        };
        var row = new ContentRow
        {
            ExternalId = item.RatingKey,
            Title = item.Title,
            Artist = item.Artist,
            Album = item.Album,
            Year = item.Year?.ToString(),
            Duration = item.DurationMilliseconds is long duration
                ? FormatSeconds(duration / 1000d)
                : string.Empty,
            Format = item.Format?.ToUpperInvariant(),
            FilePath = item.PartKeys.Count > 0
                ? PlexServerClient.CreateStreamUrl(
                    server,
                    item.PartKeys[0],
                    token)
                : string.Empty,
            PlexPartUrls = item.PartKeys
                .Select(partKey => PlexServerClient.CreateStreamUrl(server, partKey, token))
                .ToArray(),
            KnownDuration = item.DurationMilliseconds is long durationMilliseconds
                ? TimeSpan.FromMilliseconds(durationMilliseconds)
                : null,
            EntityType = entityType,
            PlexServerId = server.Id,
            PlexAlbumRatingKey = item.ParentRatingKey,
            PlexArtistRatingKey = item.GrandparentRatingKey
        };
        if (entityType == "PlexTrack" && row.FilePath.Length > 0)
            _plexTracksByUrl[row.FilePath] = row;
        return row;
    }

    /// <summary>Reveals the shared Plex table while hiding competing full-area surfaces.</summary>
    private void ShowPlexContentTable()
    {
        ContentDataGrid.IsVisible = true;
        FolderTreeView.IsVisible = false;
        AlbumArtworkListBox.IsVisible = false;
        ArtistArtworkListBox.IsVisible = false;
        SearchResultsScrollViewer.IsVisible = false;
        GenreCloudPanel.IsVisible = false;
        GenreCloudSurface.IsVisible = false;
        AiChatViewControl.IsVisible = false;
        PodcastEpisodesView.IsVisible = false;
        DashboardScrollViewer.IsVisible = false;
        InternetRadioView.IsVisible = false;
        PodcastView.IsVisible = false;
        LyricsView.IsVisible = false;
        ArtistInfoView.IsVisible = false;
        PodcastInfoView.IsVisible = false;
        PlexLoadMoreButton.IsVisible = false;
        PlexViewModeBorder.IsVisible = false;
        AlbumViewModeBorder.IsVisible = false;
        OrynivoServerViewModeBorder.IsVisible = false;
        ClearQueueButton.IsVisible = false;
        InfiniteMixButton.IsVisible = false;
        InfiniteMixActionsPanel.IsVisible = false;
        InfiniteMixStatusTextBlock.IsVisible = false;
        SaveQueueAsPlaylistButton.IsVisible = false;
        RestoreQueueButton.IsVisible = false;
        TrackFilterPopup.IsOpen = false;
        UpdateEntityFavoritesFilterToggle(null);
        SearchTextBox.IsVisible = false;
        TrackFilterButton.IsVisible = false;
        SaveSmartPlaylistButton.IsVisible = false;
        HideAlbumDetailHeader();
        UpdateLibraryIntroCard(null);
    }

    /// <summary>Captures the existing Plex binding, source identity and paging before navigation.</summary>
    /// <returns>Transient saved state, or null when no active Plex source exists.</returns>
    private PlexNavigationState? CapturePlexNavigationState() => _activePlexServer is null ? null : new(
        ContentTitleTextBlock.Text ?? string.Empty, _activePlexView,
        (ContentDataGrid.ItemsSource as IEnumerable<ContentRow>)?.ToList() ?? [],
        ContentDataGrid.SelectedItem as ContentRow, CaptureCurrentVerticalOffset(),
        _activePlexServer.Id, _activePlexServer.Name, _activePlexServer.BaseUrl,
        _activePlexToken, _activePlexSectionKey, _currentTopLevelTag, _plexDetailVisible,
        _activePlexSectionTitle, _plexLoadedCount, _plexTotalCount);

    /// <summary>Checks retained Plex Back inputs against the active and configured source.</summary>
    /// <param name="state">In-memory parent state captured before child loading.</param>
    /// <returns>Whether saved rows and position still belong to the current source.</returns>
    private bool IsCurrentPlexNavigationSource(PlexNavigationState state) =>
        _currentTopLevelTag == state.NavigationTag && _activePlexSectionKey == state.Section &&
        _activePlexToken == state.Token && _activePlexServer?.Id == state.ServerId &&
        _activePlexServer.Name == state.ServerName && _activePlexServer.BaseUrl == state.BaseUrl &&
        (_settings.PlexServers ?? []).Any(server => server.Id == state.ServerId &&
            server.Name == state.ServerName && server.BaseUrl == state.BaseUrl);

    /// <summary>Loads source-owned Plex children and retains the pre-load Back state.</summary>
    /// <param name="parent">Source-aware artist or album selected for drill-down.</param>
    /// <param name="captureParent">Whether intra-Plex Back should retain the parent view.</param>
    /// <returns>A task completing after child publication or abandonment.</returns>
    private async Task ShowPlexChildrenAsync(ContentRow parent, bool captureParent = true)
    {
        if (_activePlexServer is null || string.IsNullOrWhiteSpace(parent.ExternalId) ||
            parent.PlexServerId != _activePlexServer.Id) return;
        var parentKey = parent.ExternalId;
        var title = parent.Title ?? _activePlexSectionTitle;
        var targetView = parent.EntityType == "PlexArtist" ? "Albums" : "Tracks";
        var previousState = CapturePlexNavigationState()!;
        var context = BeginPlexLoad(null, allowSectionless: true);
        if (context is null) return;
        StatusTextBlock.Text = LocalizationManager.Current.PlexLoading;
        try
        {
            var page = await _plexClient.GetChildrenAsync(context.Server, context.Token,
                parentKey, context.Request.Token);
            if (!IsCurrentPlexLoad(context)) return;
            var rows = page.Items.Select(item => ToPlexContentRow(item, targetView,
                context.Server, context.Token)).ToList();
            if (captureParent) _plexNavigationStack.Push(previousState);
            BackButton.IsVisible = _plexNavigationStack.Count > 0 || _navigationStack.Count > 0;
            _activePlexView = targetView;
            _plexDetailVisible = true;
            ApplyColumns("Plex" + targetView);
            ContentDataGrid.ItemsSource = rows;
            ContentTitleTextBlock.Text = title;
            ContentCountTextBlock.Text = LocalizationManager.FormatEntryCount(rows.Count);
            PlexLoadMoreButton.IsVisible = false;
            PlexViewModeBorder.IsVisible = false;
            StatusTextBlock.Text = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsCurrentPlexLoad(context)) return;
            StatusTextBlock.Text = string.Format(LocalizationManager.Current.PlexConnectionFailed, ex.Message);
        }
    }

    /// <summary>Builds the Plex folder root under its captured request ownership.</summary>
    /// <param name="context">Source-owned folder view request.</param>
    /// <returns>A task completing after root publication or abandonment.</returns>
    private async Task BuildPlexFolderTreeAsync(PlexLoadContext context)
    {
        var server = context.Server;
        var token = context.Token;
        var sectionKey = context.Section!;
        var cancellationToken = context.Request.Token;
        _localFolderTrackItems.Clear();
        _localFolderTrackHeaders.Clear();
        FolderTreeView.Items.Clear();
        var page = await _plexClient.GetFoldersAsync(
            server,
            token,
            sectionKey,
            null,
            cancellationToken);
        if (!IsCurrentPlexLoad(context)) return;
        foreach (var folder in page.Items)
            FolderTreeView.Items.Add(CreatePlexFolderItem(folder, context));
    }

    /// <summary>Creates one folder node whose lazy descendants retain root ownership.</summary>
    /// <param name="item">Plex folder or track metadata.</param>
    /// <param name="context">Owning folder root request and copied source.</param>
    /// <returns>A node with guarded lazy expansion.</returns>
    private TreeViewItem CreatePlexFolderItem(PlexMediaItem item, PlexLoadContext context)
    {
        var server = context.Server;
        var token = context.Token;
        var sectionKey = context.Section!;
        var row = item.IsFolder || item.PartKeys.Count == 0
            ? null
            : ToPlexContentRow(item, "Tracks", server, token);
        var treeItem = new TreeViewItem
        {
            Header = item.Title,
            Tag = new PlexFolderTag(item.Key, row is not null, row)
        };
        ApplyNowPlayingClass(treeItem);
        if (row is not null)
        {
            treeItem.ContextFlyout = BuildQueueContextFlyout([row.FilePath]);
            AttachFolderPlaylistContextHandler(treeItem);
            return treeItem;
        }

        var placeholder = new TreeViewItem();
        treeItem.Items.Add(placeholder);
        var isLoaded = false;
        Task? loadingTask = null;

        async Task LoadChildrenAsync()
        {
            if (!IsCurrentPlexLoad(context) || isLoaded)
                return;
            if (loadingTask is not null)
            {
                await loadingTask;
                return;
            }

            loadingTask = LoadCoreAsync();
            await loadingTask;
            loadingTask = null;

            async Task LoadCoreAsync()
            {
                try
                {
                    var page = await _plexClient.GetFoldersAsync(
                        server,
                        token,
                        sectionKey,
                        item.Key,
                        context.Request.Token);
                    if (!IsCurrentPlexLoad(context)) return;
                    treeItem.Items.Clear();
                    foreach (var child in page.Items)
                        treeItem.Items.Add(CreatePlexFolderItem(child, context));
                    isLoaded = true;
                    treeItem.InvalidateMeasure();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!IsCurrentPlexLoad(context)) return;
                    treeItem.Items.Clear();
                    treeItem.Items.Add(placeholder);
                    StatusTextBlock.Text = string.Format(
                        LocalizationManager.Current.PlexConnectionFailed,
                        ex.Message);
                }
            }
        }

        treeItem.AddHandler(
            PointerPressedEvent,
            new EventHandler<PointerPressedEventArgs>(async (_, e) =>
            {
                if (!e.GetCurrentPoint(treeItem).Properties.IsLeftButtonPressed ||
                    !ReferenceEquals(FindAncestor<TreeViewItem>(e.Source as Visual), treeItem))
                {
                    return;
                }

                var isChevronPress = FindAncestor<ToggleButton>(e.Source as Visual) is not null;
                if (isChevronPress)
                {
                    if (isLoaded)
                        return;
                    e.Handled = true;
                    await LoadChildrenAsync();
                    if (isLoaded && IsCurrentPlexLoad(context))
                        treeItem.IsExpanded = true;
                    return;
                }

                if (e.ClickCount < 2)
                    return;

                e.Handled = true;
                if (treeItem.IsExpanded)
                {
                    treeItem.IsExpanded = false;
                    return;
                }

                await LoadChildrenAsync();
                if (isLoaded && IsCurrentPlexLoad(context))
                    treeItem.IsExpanded = true;
            }),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        treeItem.AddHandler(
            InputElement.DoubleTappedEvent,
            new EventHandler<TappedEventArgs>((_, e) =>
            {
                if (ReferenceEquals(FindAncestor<TreeViewItem>(e.Source as Visual), treeItem) &&
                    FindAncestor<ToggleButton>(e.Source as Visual) is null)
                    e.Handled = true;
            }),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        treeItem.Expanded += async (_, _) =>
        {
            if (isLoaded)
                return;

            treeItem.IsExpanded = false;
            await LoadChildrenAsync();
            if (isLoaded && IsCurrentPlexLoad(context))
                treeItem.IsExpanded = true;
        };
        return treeItem;
    }
}
