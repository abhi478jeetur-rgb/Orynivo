using Avalonia.Interactivity;
using Orynivo.Localization;

namespace Orynivo;

/// <summary>Shared-library empty guidance and incomplete library/search retry for the existing navigation owner.</summary>
public partial class MainWindow
{
    private int? _unifiedLibraryRetryVersion;

    /// <summary>Clears the visible notice when its owning navigation is abandoned.</summary>
    private void ClearUnifiedLibraryLoadNotice()
    {
        _unifiedLibraryLoadResult = null;
        _unifiedLibraryRetryVersion = null;
        UpdateUnifiedLibraryLoadNotice();
    }

    /// <summary>Refreshes failure/retry and successful empty-view guidance in the selected language.</summary>
    private void UpdateUnifiedLibraryLoadNotice()
    {
        var result = _unifiedLibraryLoadResult;
        var text = result is null ? string.Empty : LibraryLoadNoticeText.Format(result.Status,
            result.Sources.Count(source => source.Status is LibrarySourceLoadStatus.Failed or LibrarySourceLoadStatus.TimedOut),
            LocalizationManager.Current);
        if (SearchResultsScrollViewer.IsVisible)
            text = GetLibrarySearchNoticeText();
        LibraryLoadNotice.IsVisible = text.Length > 0;
        LibraryLoadNoticeMessage.Text = text;
        LibraryEmptyNotice.IsVisible = result is { Status: LibraryLoadStatus.Complete, Rows.Count: 0 } &&
            result.Sources.Count > 0 &&
            ((_settings.LibraryPaths?.Count ?? 0) > 0 || (_settings.OrynivoServers?.Count ?? 0) > 0) &&
            _currentTopLevelTag is "Artists" or "Albums" or "Tracks" &&
            CanReloadCurrentViewAfterLibraryChange();
        var busy = _unifiedLibraryRetryVersion.HasValue || _librarySearchRetryVersion.HasValue || _playlistRetrying;
        LibraryLoadRetryButton.IsEnabled = !busy;
        LibraryLoadRetryButton.Content = busy
            ? LocalizationManager.Current.LibraryLoadRetrying
            : LocalizationManager.Current.LibraryLoadRetry;
    }

    /// <summary>Opens source Settings for a successfully loaded empty library view.</summary>
    /// <param name="sender">The empty-view Settings action.</param>
    /// <param name="e">The activation event.</param>
    private void LibraryEmptySettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSettingsAt((_settings.LibraryPaths?.Count ?? 0) == 0 &&
            (_settings.OrynivoServers?.Count ?? 0) > 0 ? "OrynivoServers" : "Library");

    /// <summary>Retries the owning catalog or search without clearing usable rows or showing a loading overlay.</summary>
    /// <param name="sender">Retry button.</param>
    /// <param name="e">Button activation event.</param>
    private async void LibraryLoadRetryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activePlaylistId is long playlistId &&
            _unifiedLibraryLoadResult?.Status is LibraryLoadStatus.Partial or LibraryLoadStatus.Failed)
        {
            if (!_playlistRetrying) await ShowStoredPlaylistAsync(playlistId, isRetry: true);
            return;
        }
        if (SearchResultsScrollViewer.IsVisible && _librarySearchLoadResult?.Status is LibraryLoadStatus.Partial or LibraryLoadStatus.Failed)
        {
            if (_librarySearchRetryVersion.HasValue || _librarySearchQuery is null)
                return;
            await ShowLibrarySearchResultsAsync(_librarySearchQuery, _librarySearchOnlyServer, isRetry: true);
            return;
        }
        if (_unifiedLibraryRetryVersion.HasValue ||
            _unifiedLibraryLoadResult?.Status is not (LibraryLoadStatus.Partial or LibraryLoadStatus.Failed) ||
            _currentTopLevelTag is not ("Artists" or "Albums" or "Tracks") ||
            !CanReloadCurrentViewAfterLibraryChange())
            return;

        var version = _unifiedLibraryLoadVersion + 1;
        _unifiedLibraryRetryVersion = version;
        UpdateUnifiedLibraryLoadNotice();
        try
        {
            await BindLocalRowsAndStartRemoteAppendAsync(_currentTopLevelTag, preservePosition: true);
        }
        finally
        {
            if (_unifiedLibraryRetryVersion == version)
            {
                _unifiedLibraryRetryVersion = null;
                UpdateUnifiedLibraryLoadNotice();
            }
        }
    }
}
