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
/// Navigation stack, drill-down state capture/restore, and back-navigation
/// selection restoration for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly NavigationRestoreOwnership _navigationRestores = new();

    /// <summary>Resets admitted detail navigation while preserving current nested Back ownership.</summary>
    /// <param name="clearNavigationHistory">Whether saved Back states are also removed.</param>
    private void ResetDrilldownState(bool clearNavigationHistory = true)
    {
        if (!_navigationRestores.TryAdmitNavigation()) return;
        CancelProviderAlbumLoad();
        CancelTopLevelViewLoad();
        CancelRemoteViewLoad();
        CancelLibrarySearch();
        CancelAndDispose(ref _unifiedLibraryAppendCts);
        ClearUnifiedLibraryLoadNotice();
        _activeAlbumFilterId = null;
        _activeAlbumFilterTitle = null;
        _activeArtistFilterId = null;
        _activeArtistFilterName = null;
        _activeAlbumCatalogProvider = null;
        _activeCatalogAlbum = null;
        _activeLogicalAlbumIds = null;
        _activeLogicalAlbumParts = null;
        _activeLogicalAlbumRow = null;
        _plexNavigationStack.Clear();
        if (clearNavigationHistory)
            _navigationStack.Clear();
        BackButton.IsVisible = _navigationStack.Count > 0;
    }

    /// <summary>Captures independent navigation, suppresses only current nested Back capture, and abandons old loads.</summary>
    private void PushCurrentNavigationState()
    {
        if (!_navigationRestores.TryAdmitNavigation()) return;
        CancelProviderAlbumLoad();
        CancelTopLevelViewLoad();
        CancelRemoteViewLoad();
        CancelLibrarySearch();
        CancelAndDispose(ref _unifiedLibraryAppendCts);
        ClearUnifiedLibraryLoadNotice();
        if (_navigationRestores.ShouldSuppressHistory)
            return;

        var state = CaptureCurrentNavigationState();
        if (state is null)
            return;
        if (_navigationStack.TryPeek(out var previous) && previous == state)
            return;

        _navigationStack.Push(state);
        BackButton.IsVisible = true;
    }

    /// <summary>Adapts the pure capture decision to the existing window-owned Back state.</summary>
    /// <returns>The current selectable view state, or null for section-only navigation.</returns>
    private NavigationState? CaptureCurrentNavigationState()
    {
        var selectedRow = GetSelectedContentRow();
        var target = NavigationCaptureDecision.Select(new NavigationCaptureContext(
            SearchResultsScrollViewer.IsVisible, GenreCloudPanel.IsVisible, _currentTopLevelTag,
            _activeAlbumFilterId, _activeArtistFilterId, _activeArtistFilterName,
            _activeLogicalAlbumParts is { Count: > 0 },
            _activeCatalogAlbum is { Source: LibraryCatalogSource.OrynivoServer },
            _activeOrynivoServer?.Id, _activeAlbumCatalogProvider is not null));
        if (target.Kind == NavigationCaptureKind.Search)
            return new NavigationState(
                "Search",
                selectedRow?.Id,
                null,
                null,
                SearchTextBox.Text ?? string.Empty,
                CaptureCurrentVerticalOffset(),
                SelectedSourceKey: selectedRow?.SourceKey);

        if (target.Kind == NavigationCaptureKind.GenreCloud)
        {
            return new NavigationState(
                "GenreCloud",
                selectedRow?.Id,
                null,
                null,
                VerticalOffset: CaptureCurrentVerticalOffset(),
                SelectedSourceKey: selectedRow?.SourceKey,
                GenreKey: _genreCloudSelectedKey,
                GenreAlbums: GenreAlbumRecommendationsRadioButton.IsChecked == true);
        }

        if (target.Kind == NavigationCaptureKind.LogicalAlbum && _activeAlbumFilterId is long logicalAlbumId)
        {
            return new NavigationState(
                "LogicalAlbumTracks",
                logicalAlbumId,
                _activeArtistFilterId,
                _activeArtistFilterName,
                _activeAlbumFilterTitle,
                CaptureCurrentVerticalOffset(),
                LogicalAlbumParts: _activeLogicalAlbumParts);
        }

        if (target.Kind == NavigationCaptureKind.ServerAlbum && _activeAlbumFilterId is long orynivoAlbumId)
        {
            return new NavigationState(
                "OrynivoAlbumTracks",
                orynivoAlbumId,
                _activeArtistFilterId,
                _activeArtistFilterName,
                _activeAlbumFilterTitle,
                CaptureCurrentVerticalOffset(),
                target.NavigationTag,
                LogicalAlbumIds: _activeLogicalAlbumIds);
        }

        if (target.Kind == NavigationCaptureKind.LocalAlbum && _activeAlbumFilterId is long albumId)
            return new NavigationState(
                "AlbumTracks",
                albumId,
                _activeArtistFilterId,
                _activeArtistFilterName,
                _activeAlbumFilterTitle,
                LogicalAlbumIds: _activeLogicalAlbumIds);

        if (target.Kind == NavigationCaptureKind.ServerArtist && _activeArtistFilterId is long remoteArtistId)
        {
            return new NavigationState(
                "OrynivoArtistAlbums",
                selectedRow?.Id,
                remoteArtistId,
                _activeArtistFilterName,
                _activeArtistFilterName,
                CaptureCurrentVerticalOffset(),
                target.NavigationTag,
                selectedRow?.SourceKey);
        }

        if (target.Kind == NavigationCaptureKind.LocalArtist && _activeArtistFilterId is long artistId)
        {
            return new NavigationState(
                "ArtistAlbums",
                selectedRow?.Id,
                artistId,
                _activeArtistFilterName,
                _activeArtistFilterName,
                CaptureCurrentVerticalOffset(),
                SelectedSourceKey: selectedRow?.SourceKey);
        }

        if (target.Kind == NavigationCaptureKind.UnifiedArtist)
        {
            return new NavigationState(
                "UnifiedArtistAlbums",
                selectedRow?.Id,
                null,
                _activeArtistFilterName,
                _activeArtistFilterName,
                CaptureCurrentVerticalOffset(),
                SelectedSourceKey: selectedRow?.SourceKey);
        }

        if (target.Kind == NavigationCaptureKind.TopLevel && _currentTopLevelTag is { } topLevelTag)
        {
            return new NavigationState(
                topLevelTag,
                selectedRow?.Id,
                _activeArtistFilterId,
                _activeArtistFilterName,
                SearchTextBox.Text ?? string.Empty,
                CaptureCurrentVerticalOffset(),
                SelectedSourceKey: selectedRow?.SourceKey);
        }

        return null;
    }

    /// <summary>Captures the current table, search or artwork vertical position.</summary>
    /// <returns>The visible scroll offset, or null when no scroll adapter exists.</returns>
    private double? CaptureCurrentVerticalOffset()
    {
        if (SearchResultsScrollViewer.IsVisible)
            return SearchResultsScrollViewer.Offset.Y;
        if (ContentDataGrid.IsVisible)
        {
            AttachContentDataGridVerticalScrollBar();
            return _contentDataGridVerticalScrollBar?.Value;
        }

        var listBox = AlbumArtworkListBox.IsVisible
            ? AlbumArtworkListBox
            : ArtistArtworkListBox.IsVisible
                ? ArtistArtworkListBox
                : null;
        return listBox?
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault()?
            .Offset.Y;
    }

    private long? GetSelectedContentRowId()
        => GetSelectedContentRow()?.Id;

    private ContentRow? GetSelectedContentRow()
    {
        if (SearchResultsScrollViewer.IsVisible)
        {
            if (SearchTracksDataGrid.SelectedItem is ContentRow searchTrack)
                return searchTrack;
            if (SearchAlbumsDataGrid.SelectedItem is ContentRow searchAlbum)
                return searchAlbum;
            if (SearchArtistsDataGrid.SelectedItem is ContentRow searchArtist)
                return searchArtist;
            return null;
        }
        if (ContentDataGrid.IsVisible &&
            ContentDataGrid.SelectedItem is ContentRow gridRow)
            return gridRow;
        if (AlbumArtworkListBox.IsVisible &&
            AlbumArtworkListBox.SelectedItem is ContentRow albumRow)
            return albumRow;
        if (ArtistArtworkListBox.IsVisible &&
            ArtistArtworkListBox.SelectedItem is ContentRow artistRow)
            return artistRow;
        return null;
    }

    /// <summary>Restores the next existing history entry with owned delayed publication.</summary>
    /// <param name="sender">Back button.</param>
    /// <param name="e">Click event.</param>
    private async void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _navigationRestores.Invalidate();
        if (ArtistInfoView.IsVisible && !string.IsNullOrWhiteSpace(_artistInfoUnifiedArtistName))
        {
            CloseNowPlayingDetailViews();
            _artistInfoUnifiedArtistName = null;
            if (_navigationStack.Count > 0)
            {
                var artistOrigin = _navigationStack.Pop();
                await RestoreNavigationStateAsync(artistOrigin);
            }
            else BackButton.IsVisible = false;
            return;
        }

        if (LyricsView.IsVisible ||
            ArtistInfoView.IsVisible ||
            PodcastInfoView.IsVisible)
        {
            CloseNowPlayingDetailViews();
            return;
        }

        if (_plexNavigationStack.Count > 0)
        {
            await RestorePlexNavigationStateAsync();
            return;
        }

        if (_navigationStack.Count == 0)
            return;

        var state = _navigationStack.Pop();
        await RestoreNavigationStateAsync(state);
    }

    /// <summary>Restores valid Plex state or its global origin while rejecting replacement navigation.</summary>
    /// <returns>A task completing after restoration or source-safe fallback.</returns>
    private async Task RestorePlexNavigationStateAsync()
    {
        var previousContext = _navigationRestores.Context;
        var request = _navigationRestores.Begin();
        _navigationRestores.Context = request;
        bool CanRestore() => _navigationRestores.IsCurrent(request);
        try
        {
            CancelProviderAlbumLoad();
            if (!CanRestore()) return;
            CancelTopLevelViewLoad();
            if (!CanRestore()) return;
            CancelRemoteViewLoad();
            if (!CanRestore()) return;
            CancelLibrarySearch();
            if (!CanRestore()) return;
            CancelAndDispose(ref _unifiedLibraryAppendCts);
            if (!CanRestore() || _plexNavigationStack.Count == 0) return;
            var plexState = _plexNavigationStack.Pop();
            if (!IsCurrentPlexNavigationSource(plexState))
            {
                _plexNavigationStack.Clear();
                var origin = _navigationStack.Count > 0
                    ? _navigationStack.Pop()
                    : new NavigationState("Tracks", null, null, null);
                if (origin.View.StartsWith("PlexLibrary:", StringComparison.Ordinal))
                {
                    var sourceParts = origin.View.Split(':', 3);
                    if (sourceParts.Length != 3 || string.IsNullOrWhiteSpace(sourceParts[2]) ||
                        !(_settings.PlexServers ?? []).Any(server => server.Id == sourceParts[1]))
                        origin = new NavigationState("Tracks", null, null, null);
                }
                await RestoreNavigationStateCoreAsync(origin, CanRestore);
                if (CanRestore()) BackButton.IsVisible = _navigationStack.Count > 0;
                return;
            }
            _activePlexView = plexState.View;
            ContentTitleTextBlock.Text = plexState.Title;
            ApplyColumns("Plex" + plexState.View);
            ContentDataGrid.ItemsSource = plexState.Rows;
            ContentDataGrid.IsVisible = true;
            FolderTreeView.IsVisible = false;
            PlexViewModeBorder.IsVisible = _activePlexSectionKey is not null && _plexNavigationStack.Count == 0;
            PlexLoadMoreButton.IsVisible = _activePlexSectionKey is not null &&
                _plexNavigationStack.Count == 0 && _plexLoadedCount < _plexTotalCount;
            StatusTextBlock.Text = string.Empty;
            ContentCountTextBlock.Text = LocalizationManager.FormatEntryCount(plexState.Rows.Count);
            UpdateAlphabetIndex(plexState.Rows, true);
            BackButton.IsVisible = _plexNavigationStack.Count > 0 || _navigationStack.Count > 0;
            var selectedRow = plexState.Rows.FirstOrDefault(row => ReferenceEquals(row, plexState.SelectedRow));
            ContentDataGrid.SelectedItem = selectedRow;
            RestoreDataGridPositionAfterLayout(selectedRow, plexState.VerticalOffset,
                () => CanRestore() && IsCurrentPlexNavigationSource(plexState) &&
                    ReferenceEquals(ContentDataGrid.ItemsSource, plexState.Rows));
        }
        finally
        {
            _navigationRestores.Complete(request);
            _navigationRestores.Context = previousContext;
        }
    }

    /// <summary>Owns one Back execution and its deferred restoration without suppressing independent navigation.</summary>
    /// <param name="state">Saved window-owned history payload.</param>
    /// <returns>A task completing after restoration or abandonment.</returns>
    private async Task RestoreNavigationStateAsync(NavigationState state)
    {
        var previousContext = _navigationRestores.Context;
        var request = _navigationRestores.Begin();
        _navigationRestores.Context = request;
        bool CanRestore() => _navigationRestores.IsCurrent(request);
        try
        {
            CancelProviderAlbumLoad();
            if (!CanRestore()) return;
            CancelTopLevelViewLoad();
            if (!CanRestore()) return;
            CancelRemoteViewLoad();
            if (!CanRestore()) return;
            CancelLibrarySearch();
            if (!CanRestore()) return;
            CancelAndDispose(ref _unifiedLibraryAppendCts);
            if (!CanRestore()) return;
            await RestoreNavigationStateCoreAsync(state, CanRestore);
            if (CanRestore()) BackButton.IsVisible = _navigationStack.Count > 0;
        }
        finally
        {
            _navigationRestores.Complete(request);
            _navigationRestores.Context = previousContext;
        }
    }

    /// <summary>Adapts a typed restore route to the existing source-specific loaders.</summary>
    /// <param name="state">Saved window-owned history payload.</param>
    /// <param name="canRestore">Ownership retained by this Back execution.</param>
    /// <returns>A task completing after the selected loader and restoration adapter.</returns>
    private async Task RestoreNavigationStateCoreAsync(NavigationState state, Func<bool> canRestore)
    {
        if (!canRestore()) return;
        _activeArtistFilterId = state.ArtistFilterId;
        _activeArtistFilterName = state.ArtistFilterName;
        _activeAlbumFilterId = null;
        _activeAlbumFilterTitle = null;
        HideAlbumDetailHeader();

        switch (NavigationRestoreDecision.Select(new(state.View, state.SelectedId,
                    state.ArtistFilterId, state.ArtistFilterName,
                    state.LogicalAlbumParts is { Count: > 0 })))
        {
            case NavigationRestoreKind.LogicalAlbum:
                var logicalAlbumId = state.SelectedId!.Value;
                var parts = state.LogicalAlbumParts!;
                var representativePart = parts.First();
                var logicalRow = new ContentRow
                {
                    Id = logicalAlbumId,
                    AlbumId = logicalAlbumId,
                    ArtistId = representativePart.ArtistId,
                    Title = string.IsNullOrWhiteSpace(state.SearchQuery)
                        ? LocalizationManager.Current.Unknown
                        : state.SearchQuery,
                    Artist = state.ArtistFilterName,
                    EntityType = "UnifiedAlbum",
                    LogicalAlbumParts = parts,
                    OrynivoServer = representativePart.Server,
                    FilePath = ""
                };
                await OpenLogicalAlbumTracksAsync(logicalRow);
                return;

            case NavigationRestoreKind.LocalAlbum:
                var albumId = state.SelectedId!.Value;
                await ShowAlbumTracksAsync(
                    albumId,
                    string.IsNullOrWhiteSpace(state.SearchQuery)
                        ? LocalizationManager.Current.Unknown
                        : state.SearchQuery,
                    state.ArtistFilterId,
                    state.ArtistFilterName,
                    state.LogicalAlbumIds);
                return;

            case NavigationRestoreKind.LocalArtist:
                var artistId = state.ArtistFilterId!.Value;
                var localArtistName = string.IsNullOrWhiteSpace(state.SearchQuery)
                    ? LocalizationManager.Current.Unknown : state.SearchQuery;
                var localArtistRestore = ShowArtistAlbumsAsync(artistId, localArtistName);
                var localArtistVersion = _artistDetailLoadVersion;
                await localArtistRestore;
                RestoreSelectionFromCurrentItems(
                    state.SelectedId,
                    state.VerticalOffset,
                    state.SelectedSourceKey,
                    () => canRestore() && localArtistVersion == _artistDetailLoadVersion &&
                          IsCurrentArtistDetailLoad(localArtistVersion, localArtistName));
                return;

            case NavigationRestoreKind.UnifiedArtist:
                var unifiedArtistRestore = ShowUnifiedArtistAlbumsAsync(state.ArtistFilterName!);
                var unifiedArtistVersion = _artistDetailLoadVersion;
                await unifiedArtistRestore;
                RestoreSelectionFromCurrentItems(state.SelectedId, state.VerticalOffset, state.SelectedSourceKey,
                    () => canRestore() && IsCurrentArtistDetailLoad(unifiedArtistVersion, state.ArtistFilterName!));
                return;

            case NavigationRestoreKind.ServerAlbum:
                var serverAlbumId = state.SelectedId!.Value;
                await RestoreOrynivoAlbumTracksAsync(
                    state.NavigationTag,
                    serverAlbumId,
                    state.SearchQuery,
                    state.ArtistFilterId,
                    state.ArtistFilterName,
                    state.VerticalOffset,
                    state.LogicalAlbumIds);
                return;

            case NavigationRestoreKind.ServerArtist:
                var serverArtistId = state.ArtistFilterId!.Value;
                await RestoreOrynivoArtistAlbumsAsync(
                    state.NavigationTag,
                    serverArtistId,
                    state.SearchQuery,
                    state.SelectedId,
                    state.VerticalOffset);
                return;

            case NavigationRestoreKind.Search:
                SearchTextBox.Text = state.SearchQuery ?? string.Empty;
                var searchRestore = ShowSearchResultsAsync(state.SearchQuery ?? string.Empty);
                var searchVersion = _librarySearchVersion;
                var searchGeneration = _unifiedLibraryLoads.Generation;
                var searchToken = _librarySearchCts?.Token;
                await searchRestore;
                RestoreSearchSelection(state.SelectedId, state.SelectedSourceKey, state.VerticalOffset,
                    () => canRestore() && searchToken.HasValue && !searchToken.Value.IsCancellationRequested &&
                          searchVersion == _librarySearchVersion &&
                          searchGeneration == _unifiedLibraryLoads.Generation && SearchResultsScrollViewer.IsVisible);
                return;

            case NavigationRestoreKind.GenreCloud:
                _genreCloudSelectedKey = state.GenreKey;
                GenreAlbumRecommendationsRadioButton.IsChecked = state.GenreAlbums == true;
                GenreTrackRecommendationsRadioButton.IsChecked = state.GenreAlbums != true;
                await RestoreTopLevelNavigationAsync(state, canRestore);
                return;

            default:
                await RestoreTopLevelNavigationAsync(state, canRestore);
                break;
        }
    }

    /// <summary>Restores a top-level binding and layout only while its exact loader and Back execution remain current.</summary>
    /// <param name="state">Saved sidebar view, selection and scroll payload.</param>
    /// <param name="canRestore">Owning Back execution guard.</param>
    /// <returns>A task completing after load and guarded selection admission.</returns>
    private async Task RestoreTopLevelNavigationAsync(NavigationState state, Func<bool> canRestore)
    {
        if (!canRestore()) return;
        SelectNavigationItem(state.View);
        var generation = _unifiedLibraryLoads.Generation;
        var token = new CancellationToken(canceled: true);
        await LoadTopLevelViewAsync(state.View, admitted => token = admitted);
        bool CanRestorePosition() => canRestore() && !token.IsCancellationRequested &&
            generation == _unifiedLibraryLoads.Generation &&
            string.Equals(_currentTopLevelTag, state.View, StringComparison.Ordinal);
        RestoreSelectionFromCurrentItems(state.SelectedId, state.VerticalOffset,
            state.SelectedSourceKey, CanRestorePosition);
    }

    private void SelectNavigationItem(string tag)
    {
        var item = NavListBox.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(navItem => string.Equals(navItem.Tag as string, tag, StringComparison.Ordinal));
        if (item is null || ReferenceEquals(NavListBox.SelectedItem, item))
            return;

        _suppressNavSelectionChanged = true;
        try
        {
            NavListBox.SelectedItem = item;
        }
        finally
        {
            _suppressNavSelectionChanged = false;
        }
    }

    /// <summary>Restores a source-aware selection and scroll position from the current binding.</summary>
    /// <param name="selectedId">Selected entity ID, when available.</param>
    /// <param name="verticalOffset">Previously visible vertical position.</param>
    /// <param name="selectedSourceKey">Source identity disambiguating matching IDs.</param>
    /// <param name="canRestore">Optional guard rejecting restoration after navigation or invalidation.</param>
    private void RestoreSelectionFromCurrentItems(
        long? selectedId,
        double? verticalOffset = null,
        string? selectedSourceKey = null,
        Func<bool>? canRestore = null)
    {
        if (canRestore is not null && !canRestore())
            return;
        var rows = (ContentDataGrid.ItemsSource as IEnumerable<ContentRow>)?.ToList()
                   ?? (AlbumArtworkListBox.ItemsSource as IEnumerable<ContentRow>)?.ToList()
                   ?? (ArtistArtworkListBox.ItemsSource as IEnumerable<ContentRow>)?.ToList()
                   ?? [];
        RestoreSelection(rows, selectedId, verticalOffset, selectedSourceKey, canRestore);
    }

    /// <summary>Restores a source-aware selection using the existing table or artwork controls.</summary>
    /// <param name="rows">Published content rows.</param>
    /// <param name="selectedId">Selected entity ID, when available.</param>
    /// <param name="verticalOffset">Previously visible vertical position.</param>
    /// <param name="selectedSourceKey">Source identity disambiguating matching IDs.</param>
    /// <param name="canRestore">Optional guard rejecting stale restoration.</param>
    private void RestoreSelection(
        List<ContentRow> rows,
        long? selectedId,
        double? verticalOffset = null,
        string? selectedSourceKey = null,
        Func<bool>? canRestore = null)
    {
        if (canRestore is not null && !canRestore())
            return;
        var row = selectedId is long id
            ? rows.FirstOrDefault(candidate =>
                candidate.Id == id &&
                (selectedSourceKey is null ||
                 string.Equals(candidate.SourceKey, selectedSourceKey, StringComparison.OrdinalIgnoreCase)))
            : null;

        if (ContentDataGrid.IsVisible)
        {
            ContentDataGrid.SelectedItem = row;
            RestoreDataGridPositionAfterLayout(row, verticalOffset, canRestore);
            return;
        }

        var listBox = AlbumArtworkListBox.IsVisible
            ? AlbumArtworkListBox
            : ArtistArtworkListBox.IsVisible
                ? ArtistArtworkListBox
                : null;
        if (listBox is null)
            return;

        if (row is not null)
        {
            EnsureArtworkRowBound(listBox, row);
            listBox.SelectedItem = row;
        }
        RestoreArtworkPositionAfterLayout(listBox, row, verticalOffset, canRestore);
    }

    /// <summary>Restores a search result selection and scroll position while its optional owning load is current.</summary>
    /// <param name="selectedId">Selected provider-local entity ID.</param>
    /// <param name="selectedSourceKey">Source identity disambiguating matching IDs.</param>
    /// <param name="verticalOffset">Outer search page scroll offset.</param>
    /// <param name="canRestore">Optional deferred restoration guard.</param>
    /// <param name="selectedEntityType">Optional category identity for an in-place retry.</param>
    private void RestoreSearchSelection(
        long? selectedId,
        string? selectedSourceKey,
        double? verticalOffset,
        Func<bool>? canRestore = null,
        string? selectedEntityType = null)
    {
        if (canRestore is not null && !canRestore())
            return;
        if (selectedId is long id)
        {
            foreach (var grid in new[] { SearchTracksDataGrid, SearchAlbumsDataGrid, SearchArtistsDataGrid })
            {
                var row = (grid.ItemsSource as IEnumerable<ContentRow>)?.FirstOrDefault(candidate =>
                    candidate.Id == id &&
                    (selectedEntityType is null || candidate.EntityType == selectedEntityType) &&
                    (selectedSourceKey is null ||
                     string.Equals(candidate.SourceKey, selectedSourceKey, StringComparison.OrdinalIgnoreCase)));
                if (row is null)
                    continue;
                grid.SelectedItem = row;
                grid.ScrollIntoView(row, null);
                break;
            }
        }
        if (verticalOffset is double offset)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (canRestore is not null && !canRestore())
                        return;
                    SearchResultsScrollViewer.Offset = new Vector(
                        SearchResultsScrollViewer.Offset.X,
                        Math.Clamp(
                            offset,
                            0,
                            Math.Max(0, SearchResultsScrollViewer.Extent.Height -
                                        SearchResultsScrollViewer.Viewport.Height)));
                },
                DispatcherPriority.Loaded);
        }
    }

    /// <summary>Restores table position after layout only while its owning load remains current.</summary>
    /// <param name="row">Selected content row.</param>
    /// <param name="verticalOffset">Previously visible vertical position.</param>
    /// <param name="canRestore">Optional guard evaluated when the dispatcher callback runs.</param>
    private void RestoreDataGridPositionAfterLayout(
        ContentRow? row,
        double? verticalOffset,
        Func<bool>? canRestore = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (canRestore is not null && !canRestore())
                return;
            AttachContentDataGridVerticalScrollBar();
            if (verticalOffset is double offset &&
                _contentDataGridVerticalScrollBar is { } scrollBar)
            {
                scrollBar.Value = Math.Clamp(offset, scrollBar.Minimum, scrollBar.Maximum);
            }
            else if (row is not null)
            {
                ContentDataGrid.ScrollIntoView(row, null);
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Restores artwork position after layout while both binding and load remain current.</summary>
    /// <param name="listBox">Active artwork list.</param>
    /// <param name="row">Selected content row.</param>
    /// <param name="verticalOffset">Previously visible vertical position.</param>
    /// <param name="canRestore">Optional guard evaluated before deferred restoration.</param>
    private void RestoreArtworkPositionAfterLayout(
        ListBox listBox,
        ContentRow? row,
        double? verticalOffset,
        Func<bool>? canRestore = null)
    {
        var bindingVersion = _artworkBindingVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (bindingVersion != _artworkBindingVersion || (canRestore is not null && !canRestore()))
                return;

            var scrollViewer = listBox.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
            if (scrollViewer is null)
                return;

            if (verticalOffset is double offset)
            {
                var itemWidth = GetArtworkItemWidth(listBox);
                var itemHeight = GetArtworkItemHeight(listBox);
                var perRow = Math.Max(1, (int)Math.Floor(scrollViewer.Viewport.Width / itemWidth));
                var requiredItems = Math.Max(
                    ArtworkPageSize,
                    ((int)Math.Ceiling((offset + scrollViewer.Viewport.Height) / itemHeight) + 1) * perRow);
                var visibleRows = ReferenceEquals(listBox, AlbumArtworkListBox)
                    ? _visibleAlbumArtworkRows
                    : _visibleArtistArtworkRows;
                while (visibleRows.Count < requiredItems && AppendArtworkRows(listBox))
                {
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (bindingVersion != _artworkBindingVersion || (canRestore is not null && !canRestore()))
                        return;
                    var restoredViewer = listBox.GetVisualDescendants()
                        .OfType<ScrollViewer>()
                        .FirstOrDefault();
                    if (restoredViewer is null)
                        return;
                    restoredViewer.Offset = new Vector(
                        restoredViewer.Offset.X,
                        Math.Clamp(offset, 0, Math.Max(0, restoredViewer.Extent.Height - restoredViewer.Viewport.Height)));
                    QueueHydrateVisibleArtworkRows(listBox);
                    UpdateActiveAlphabetButton();
                }, DispatcherPriority.Background);
            }
            else if (row is not null)
            {
                ScrollArtworkRowIntoViewAfterLayout(listBox, row, canRestore);
            }
        }, DispatcherPriority.Loaded);
    }
}
