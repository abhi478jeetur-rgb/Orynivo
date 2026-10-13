using Avalonia.Controls;
using Avalonia.Layout;
using Orynivo.Library;

namespace Orynivo;

/// <summary>Incremental session-cached Dashboard publication.</summary>
public partial class MainWindow
{
    private readonly ContentControl[] _dashboardSections =
        [new() { HorizontalContentAlignment = HorizontalAlignment.Stretch },
         new() { HorizontalContentAlignment = HorizontalAlignment.Stretch },
         new() { HorizontalContentAlignment = HorizontalAlignment.Stretch },
         new() { HorizontalContentAlignment = HorizontalAlignment.Stretch }];
    private readonly string?[] _dashboardSectionFingerprints = new string?[4];
    private readonly object?[] _dashboardSectionInputs = new object?[4];
    private readonly Action?[] _dashboardSectionBuilders = new Action?[4];
    private DashboardLibrarySummary _dashboardLastSummary = new(0, 0, 0, 0);
    private readonly Dictionary<string, object> _dashboardRefreshTasks = new(StringComparer.Ordinal);

    /// <summary>Coalesces overlapping source refreshes without storing another completed catalog.</summary>
    /// <typeparam name="T">Source result type.</typeparam>
    /// <param name="phase">Fixed phase identity.</param>
    /// <param name="load">Source adapter captured on the UI thread.</param>
    /// <returns>The shared in-flight result.</returns>
    private Task<T> ShareDashboardRefreshAsync<T>(string phase, Func<Task<T>> load)
    {
        var key = $"{Volatile.Read(ref _dashboardCatalogGeneration)};{_dashboardRecommendationPeriod};" +
            $"{_dashboardRecommendationMood};{phase}";
        if (_dashboardRefreshTasks.TryGetValue(key, out var active)) return (Task<T>)active;
        var completion = new TaskCompletionSource<T>();
        _dashboardRefreshTasks[key] = completion.Task;
        _ = CompleteAsync();
        return completion.Task;
        async Task CompleteAsync()
        {
            try { completion.SetResult(await load()); }
            catch (Exception exception) { completion.SetException(exception); }
            finally
            {
                if (_dashboardRefreshTasks.TryGetValue(key, out var current) && ReferenceEquals(current, completion.Task))
                    _dashboardRefreshTasks.Remove(key);
            }
        }
    }

    /// <summary>Replaces only a changed Dashboard section, retaining unchanged controls.</summary>
    /// <param name="index">Stable section slot.</param>
    /// <param name="inputs">Transient display inputs.</param>
    /// <param name="build">Synchronous builder using the existing Dashboard panel adapter.</param>
    private void PublishDashboardSection(int index, object inputs, Action build)
    {
        var fingerprint = ViewPreviewStore.Fingerprint(new
        {
            Data = inputs,
            _settings.Language,
            _settings.Theme,
            _dashboardTwoColumnLayout,
            _settings.ReduceMotion
        });
        if (_dashboardSectionFingerprints[index] == fingerprint)
            return;
        var visible = DashboardPanel;
        var staging = new StackPanel { Spacing = visible.Spacing };
        DashboardPanel = staging;
        try { build(); }
        finally { DashboardPanel = visible; }
        _dashboardSections[index].Content = staging;
        _dashboardSectionFingerprints[index] = fingerprint;
        _dashboardSectionInputs[index] = inputs;
        _dashboardSectionBuilders[index] = build;
    }

    /// <summary>Reflows or relocalizes retained data immediately without waiting for source refresh.</summary>
    private void RefreshDashboardPresentation()
    {
        for (var index = 0; index < _dashboardSections.Length; index++)
            if (_dashboardSectionInputs[index] is { } inputs && _dashboardSectionBuilders[index] is { } build)
                PublishDashboardSection(index, inputs, build);
    }

    /// <summary>Drops retained UI after source/profile changes, without retaining another user's data.</summary>
    private void ClearDashboardPresentationCache()
    {
        ++_dashboardBuildVersion;
        foreach (var section in _dashboardSections)
            section.Content = null;
        Array.Clear(_dashboardSectionFingerprints);
        Array.Clear(_dashboardSectionInputs);
        Array.Clear(_dashboardSectionBuilders);
        _dashboardRefreshTasks.Clear();
        _dashboardLastSummary = new(0, 0, 0, 0);
    }

    /// <summary>Projects album display and source identities for unchanged-section comparison.</summary>
    /// <param name="albums">Current ranked album data.</param>
    /// <returns>Transient comparable display values without connection credentials.</returns>
    private static object DashboardAlbumDisplayInputs(List<DashboardAlbum> albums) =>
        albums.Select(album => new
        {
            album.Id,
            album.Title,
            album.Artist,
            album.ArtistId,
            album.AddedAt,
            Source = album.Server?.Id,
            album.ArtworkPath,
            album.HasArtwork,
            album.IsFavorite,
            album.LogicalAlbumIds,
            Parts = album.LogicalAlbumParts?.Select(part => new { part.AlbumId, Source = part.Server?.Id })
        }).ToArray();
}
