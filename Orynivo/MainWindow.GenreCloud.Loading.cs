using Avalonia.Threading;
using Orynivo.Localization;

namespace Orynivo;

/// <summary>Immediate foreground feedback for uncached cloud branch selection.</summary>
public partial class MainWindow
{
    private readonly Dictionary<string, CompletedWorkProgress> _genreCloudLoadProgress = new(StringComparer.Ordinal);
    private NavigationLoadOwnership.Request? _genreCloudLoadingOwner;
    private DispatcherTimer? _genreCloudLoadingTimer;

    /// <summary>Hides old cloud/results immediately and presents owned assembly progress.</summary>
    /// <param name="request">Foreground publication owner.</param>
    /// <param name="loadKey">Captured shared snapshot work identity.</param>
    /// <param name="canPublish">Original foreground publication guard.</param>
    private void BeginGenreCloudLoading(NavigationLoadOwnership.Request request, string loadKey, Func<bool> canPublish)
    {
        _genreCloudLoadingTimer?.Stop();
        _genreCloudLoadingOwner = request;
        GenreCloudDisplay.IsVisible = false;
        GenreCloudLoadingPanel.IsVisible = true;
        GenreCloudLoadingProgressBar.Value = 0;
        GenreCloudLoadingProgressBar.IsIndeterminate = true;
        GenreCloudLoadingProgressTextBlock.Text = string.Empty;
        ContentDataGrid.IsVisible = false;
        AlbumArtworkListBox.IsVisible = false;
        _genreCloudLoadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _genreCloudLoadingTimer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_genreCloudLoadingOwner, request)) return;
            if (!canPublish()) { HideGenreCloudLoading(request); return; }
            CompletedWorkProgress? progress;
            lock (_genreCloudViewCacheSync) _genreCloudLoadProgress.TryGetValue(loadKey, out progress);
            if (progress is null) return;
            GenreCloudLoadingProgressBar.IsIndeterminate = false;
            GenreCloudLoadingProgressBar.Value = progress.Percentage;
            GenreCloudLoadingProgressTextBlock.Text = string.Format(
                LocalizationManager.Current.GenreCloudLoadingProgress, progress.Percentage);
        };
        _genreCloudLoadingTimer.Start();
    }

    /// <summary>Releases only the original loading owner, including cancelled navigation.</summary>
    /// <param name="request">Expected owner, or <see langword="null"/> for explicit view abandonment.</param>
    private void HideGenreCloudLoading(NavigationLoadOwnership.Request? request = null)
    {
        if (request is not null && !ReferenceEquals(_genreCloudLoadingOwner, request)) return;
        _genreCloudLoadingTimer?.Stop();
        _genreCloudLoadingTimer = null;
        _genreCloudLoadingOwner = null;
        GenreCloudLoadingPanel.IsVisible = false;
        GenreCloudDisplay.IsVisible = true;
        if (_currentTopLevelTag == "GenreCloud" && GenreCloudPanel.IsVisible)
            ApplyGenreRecommendationMode();
    }
}
