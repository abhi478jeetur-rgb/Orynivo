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
/// Track facet filters and unified smart-playlist criteria, candidates, and
/// resolution for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow : Window
{
    private async void TrackFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RefreshTrackFilterPopupAsync();
        TrackFilterPopup.IsOpen = !TrackFilterPopup.IsOpen;
    }

    private async Task RefreshTrackFilterPopupAsync()
    {
        var facets = await BuildTrackFilterFacetsAsync();

        TrackFilterPanel.Children.Clear();
        AddTrackFilterCheckBox(
            TrackFilterPanel,
            LocalizationManager.Current.Favorites,
            facets.Count(f => f.IsFavorite && MatchesTrackFilters(f, ignoredDimension: "favorite")),
            _trackFavoritesOnly,
            isChecked => _trackFavoritesOnly = isChecked);

        var genreSection = AddTrackFilterSection(LocalizationManager.Current.Genre);
        foreach (var option in BuildGenreFacetCounts(facets))
            AddTrackFilterCheckBox(
                genreSection,
                option.Key,
                option.Value,
                _selectedTrackGenres.Contains(option.Key),
                isChecked => ToggleSelection(_selectedTrackGenres, option.Key, isChecked));

        var audioTypeSection = AddTrackFilterSection(LocalizationManager.Current.AudioTypes);
        foreach (var option in BuildStringFacetCounts(facets, f => f.Format, "format"))
            AddTrackFilterCheckBox(
                audioTypeSection,
                option.Key.ToUpperInvariant(),
                option.Value,
                _selectedTrackFormats.Contains(option.Key),
                isChecked => ToggleSelection(_selectedTrackFormats, option.Key, isChecked));

        var bitrateSection = AddTrackFilterSection(LocalizationManager.Current.Bitrate);
        foreach (var option in BuildBitrateFacetCounts(facets))
            AddTrackFilterCheckBox(
                bitrateSection,
                $"{option.Key:N0} kbps",
                option.Value,
                _selectedTrackBitrates.Contains(option.Key),
                isChecked => ToggleSelection(_selectedTrackBitrates, option.Key, isChecked));

        var sourceSection = AddTrackFilterSection(LocalizationManager.Current.SourceColumn);
        foreach (var option in BuildSourceFacetCounts(facets))
            AddTrackFilterCheckBox(
                sourceSection,
                GetSourceDisplayName(option.Key),
                option.Value,
                _selectedTrackSources.Contains(option.Key),
                isChecked => ToggleSelection(_selectedTrackSources, option.Key, isChecked));
    }

    private async Task<List<TrackFacetInfo>> BuildTrackFilterFacetsAsync()
    {
        if (_orynivoTrackFacets is not null)
            return _orynivoTrackFacets;

        var facets = await Task.Run(() =>
        {
            try
            {
                using var db = AudioDatabase.OpenDefault();
                return db.GetTrackFacets();
            }
            catch
            {
                return [];
            }
        });

        if (_currentTopLevelTag != "Tracks")
            return facets;

        foreach (var server in _settings.OrynivoServers ?? [])
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var serverFacets = await _orynivoClient.GetTrackFacetsAsync(server, cts.Token);
                facets.AddRange(serverFacets.Select(facet => facet with
                {
                    IsFavorite = IsOrynivoFavorite(server, "Track", facet.Id),
                    SourceKey = GetServerSourceKey(server.Id)
                }));
            }
            catch
            {
                // Unavailable servers simply do not contribute filter counts.
            }
        }

        return facets;
    }

    private StackPanel AddTrackFilterSection(string title)
    {
        var content = new StackPanel();
        var expander = new Expander
        {
            Header = title,
            Content = content,
            IsExpanded = _expandedTrackFilterSections.Contains(title),
            Margin = TrackFilterPanel.Children.Count == 0 ? new Thickness(0, 0, 0, 0) : new Thickness(0, 8, 0, 0),
            Theme = FindResource<ControlTheme>("TrackFilterExpanderTheme")
        };
        expander.Expanded += (_, _) => _expandedTrackFilterSections.Add(title);
        expander.Collapsed += (_, _) => _expandedTrackFilterSections.Remove(title);
        TrackFilterPanel.Children.Add(expander);
        return content;
    }

    private void AddTrackFilterCheckBox(StackPanel section, string label, int count, bool isChecked, Action<bool> update)
    {
        var content = new Grid();
        content.Margin = new Thickness(8, 0, 0, 0);
        content.Width = 530;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        content.Children.Add(new TextBlock
        {
            Text = label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource<IBrush>("AppPrimaryTextBrush")
        });
        var countText = new TextBlock
        {
            Text = count.ToString("N0"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource<IBrush>("AppMutedTextBrush")
        };
        Grid.SetColumn(countText, 1);
        content.Children.Add(countText);

        var checkBox = new CheckBox
        {
            Content = content,
            IsChecked = isChecked,
            Margin = new Thickness(0, 6, 0, 6),
            Foreground = FindResource<IBrush>("AppPrimaryTextBrush"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Theme = FindResource<ControlTheme>("HeaderCheckBoxTheme")
        };
        checkBox.IsCheckedChanged += async (_, _) => await OnTrackFilterChangedAsync(update, checkBox.IsChecked == true);
        section.Children.Add(checkBox);
    }

    private async Task OnTrackFilterChangedAsync(Action<bool> update, bool isChecked)
    {
        update(isChecked);
        if (_orynivoTrackFacets is not null && _activeOrynivoServer is not null)
        {
            await ApplyOrynivoTrackFiltersAsync();
        }
        else if (_currentTopLevelTag == "Tracks")
        {
            await BindLocalRowsAndStartRemoteAppendAsync("Tracks");
            UpdateSaveSmartPlaylistButtonState();
        }
        else
        {
            var rows = await Task.Run(GetFilteredTrackRows);
            ContentDataGrid.ItemsSource = rows;
            UpdateAlphabetIndex(rows, true);
            ContentCountTextBlock.Text = LocalizationManager.FormatEntryCount(rows.Count);
            UpdateSaveSmartPlaylistButtonState();
        }
        await RefreshTrackFilterPopupAsync();
    }

    private static void ToggleSelection<T>(HashSet<T> values, T value, bool isChecked)
    {
        if (isChecked)
            values.Add(value);
        else
            values.Remove(value);
    }

    /// <summary>Evaluates live facets using the same rules as captured asynchronous search filters.</summary>
    /// <param name="facet">Track facet metadata.</param>
    /// <param name="ignoredDimension">Optional dimension omitted from option counts.</param>
    /// <returns>Whether the non-ignored dimensions match.</returns>
    private bool MatchesTrackFilters(TrackFacetInfo facet, string? ignoredDimension = null) =>
        TrackFacetFilterSnapshot.Matches(facet, _trackFavoritesOnly, _selectedTrackGenres,
            _selectedTrackFormats, _selectedTrackBitrates, _selectedTrackSources, ignoredDimension);

    /// <summary>Maps compact remote metadata to the shared criteria model with a transient identity.</summary>
    /// <param name="id">Negative runtime candidate ID.</param>
    /// <param name="track">Captured provider metadata and personal favorite state.</param>
    /// <param name="sourceKey">Opaque owning playlist provider key.</param>
    /// <returns>A compact criteria candidate without a playback URL.</returns>
    private static SmartPlaylistTrackInfo ToSmartPlaylistCandidate(long id, LibraryCatalogTrack track, string sourceKey) =>
        new(
            id,
            track.IsFavorite,
            track.Genre,
            track.Format,
            track.Bitrate,
            track.Year,
            track.Artist,
            track.Album,
            track.Duration,
            track.AddedAt ?? 0,
            PlayCount: 0,
            LastPlayedAt: null,
            track.SortTitle?.Trim() ?? track.Title?.Trim() ?? track.FileName.Trim(),
            sourceKey);

    private bool HasActiveFilters =>
        _trackFavoritesOnly || _selectedTrackGenres.Count > 0 ||
        _selectedTrackFormats.Count > 0 || _selectedTrackBitrates.Count > 0 ||
        _selectedTrackSources.Count > 0;

    private void UpdateSaveSmartPlaylistButtonState()
    {
        // A smart playlist can also capture just the search text, so the save action stays
        // available when the Tracks search box holds a query even without facet filters.
        SaveSmartPlaylistButton.IsEnabled =
            HasActiveFilters || !string.IsNullOrWhiteSpace(SearchTextBox.Text);
    }

    /// <summary>
    /// Builds smart-playlist criteria from the currently active Tracks facet filters.
    /// Shared by the local and remote Orynivo Server smart-playlist save paths.
    /// </summary>
    /// <returns>The criteria mirroring the active favourite/genre/format/bitrate facets.</returns>
    private SmartPlaylistCriteria BuildCurrentTrackFilterCriteria() => new()
    {
        FavoritesOnly = _trackFavoritesOnly,
        SearchText = string.IsNullOrWhiteSpace(SearchTextBox.Text) ? null : SearchTextBox.Text.Trim(),
        Genres = [.. _selectedTrackGenres.OrderBy(g => g)],
        Formats = [.. _selectedTrackFormats.OrderBy(f => f)],
        Bitrates = [.. _selectedTrackBitrates.OrderBy(b => b)],
        SourceKeys = [.. _selectedTrackSources.OrderBy(s => s)]
    };

    private async void SaveSmartPlaylistButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewPlaylistDialog();
        if (await dialog.ShowDialog<bool>(this) == false || string.IsNullOrWhiteSpace(dialog.PlaylistName))
            return;

        var json = JsonSerializer.Serialize(BuildCurrentTrackFilterCriteria());
        var name = dialog.PlaylistName.Trim();

        // Remote Tracks view: persist the smart playlist on the active server.
        if (_orynivoTrackFacets is not null && _activeOrynivoServer is { } server)
        {
            var created = await _orynivoClient.CreateSmartPlaylistAsync(server, name, json);
            if (created is null)
            {
                StatusTextBlock.Text = LocalizationManager.Current.OrynivoConnectionFailed;
                return;
            }

            LoadOrynivoServerNavigation();
            StatusTextBlock.Text = string.Format(LocalizationManager.Current.SmartPlaylistSaved, name);
            return;
        }

        try
        {
            using var db = AudioDatabase.OpenDefault();
            db.CreateSmartPlaylist(name, json);
        }
        catch { return; }

        LoadNavPlaylists();
        StatusTextBlock.Text = string.Format(LocalizationManager.Current.SmartPlaylistSaved, name);
    }

    private IEnumerable<KeyValuePair<string, int>> BuildGenreFacetCounts(IEnumerable<TrackFacetInfo> facets)
    {
        var counts = facets
            .Where(f => MatchesTrackFilters(f, "genre"))
            .SelectMany(f => SplitGenres(f.Genre))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var selected in _selectedTrackGenres)
            counts.TryAdd(selected, 0);
        return counts
            .Select(x => new KeyValuePair<string, int>(x.Key, x.Value))
            .OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase);
    }

    private IEnumerable<KeyValuePair<string, int>> BuildStringFacetCounts(
        IEnumerable<TrackFacetInfo> facets,
        Func<TrackFacetInfo, string?> selector,
        string ignoredDimension)
    {
        var counts = facets
            .Where(f => MatchesTrackFilters(f, ignoredDimension))
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var selected in _selectedTrackFormats)
            counts.TryAdd(selected, 0);
        return counts
            .Select(x => new KeyValuePair<string, int>(x.Key, x.Value))
            .OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase);
    }

    private IEnumerable<KeyValuePair<int, int>> BuildBitrateFacetCounts(IEnumerable<TrackFacetInfo> facets)
    {
        var counts = facets
            .Where(f => MatchesTrackFilters(f, "bitrate"))
            .Where(f => f.Bitrate.HasValue)
            .GroupBy(f => f.Bitrate!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var selected in _selectedTrackBitrates)
            counts.TryAdd(selected, 0);
        return counts
            .Select(x => new KeyValuePair<int, int>(x.Key, x.Value))
            .OrderBy(x => x.Key);
    }

    private IEnumerable<KeyValuePair<string, int>> BuildSourceFacetCounts(IEnumerable<TrackFacetInfo> facets)
    {
        var counts = facets
            .Where(f => MatchesTrackFilters(f, "source"))
            .GroupBy(f => f.SourceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var selected in _selectedTrackSources)
            counts.TryAdd(selected, 0);
        return counts
            .Select(x => new KeyValuePair<string, int>(x.Key, x.Value))
            .OrderBy(x => GetSourceDisplayName(x.Key), StringComparer.CurrentCultureIgnoreCase);
    }

    private string GetSourceDisplayName(string sourceKey)
    {
        if (string.Equals(sourceKey, LocalSourceKey, StringComparison.OrdinalIgnoreCase))
            return LocalizationManager.Current.LocalSource;
        const string serverPrefix = "server:";
        if (sourceKey.StartsWith(serverPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var serverId = sourceKey[serverPrefix.Length..];
            var server = _settings.OrynivoServers?.FirstOrDefault(item =>
                string.Equals(item.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (server is not null)
                return server.Name;
        }

        return sourceKey;
    }

    private static IEnumerable<string> SplitGenres(string? genre)
        => string.IsNullOrWhiteSpace(genre)
            ? []
            : genre.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
