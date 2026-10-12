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
/// Local and Orynivo Server artist rename and merge flows.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Opens the rename flow for the displayed local or remote artist.</summary>
    /// <param name="sender">The rename button.</param>
    /// <param name="e">The routed click event.</param>
    private async void EditArtistNameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_artistInfoDisplayedRemoteRow is { } remoteRow)
        {
            await EditOrynivoArtistNameAsync(remoteRow);
            return;
        }
        if (_artistInfoDisplayedId is long artistId)
            await EditLocalArtistNameAsync(artistId,
                dialog => dialog.ShowDialog<bool>(this),
                dialog => dialog.ShowDialog<bool>(this));
    }

    /// <summary>Completes accepted local edits while retaining the original detail for UI publication and merge admission.</summary>
    /// <param name="artistId">The original local artist identity.</param>
    /// <param name="showEdit">Shows the rename confirmation and returns whether it was accepted.</param>
    /// <param name="showMerge">Shows an explicit merge survivor choice.</param>
    /// <returns>A task completing the accepted data work and still-current detail refresh.</returns>
    private async Task EditLocalArtistNameAsync(long artistId,
        Func<EditArtistNameDialog, Task<bool>> showEdit,
        Func<ArtistMergeDialog, Task<bool>> showMerge)
    {
        var displayedId = _artistInfoDisplayedId;
        var displayedTitle = ArtistInfoTitleButton.Content as string;
        var tag = _currentTopLevelTag;
        var detailVersion = _artistDetailLoadVersion;
        var unifiedName = _artistInfoUnifiedArtistName;
        var observation = _artistProfileLoads.CaptureCurrent();
        var restore = _navigationRestores.Context;
        var trackId = _currentTrackId;
        var trackPath = _currentFilePath;
        var provider = _currentNowPlayingProvider;
        bool CanPublish() => ArtistInfoView.IsVisible &&
            _artistInfoDisplayedRemoteRow is null && !_nowPlayingRemoteArtistInfo &&
            _artistInfoDisplayedId == displayedId && ArtistInfoTitleButton.Content as string == displayedTitle &&
            _currentTopLevelTag == tag && _artistDetailLoadVersion == detailVersion &&
            _artistInfoUnifiedArtistName == unifiedName && _artistProfileLoads.IsCurrent(observation) &&
            (restore is null || _navigationRestores.IsCurrent(restore));
        if (displayedId != artistId || !CanPublish()) return;
        var artist = await Task.Run(() =>
        {
            using var db = AudioDatabase.OpenDefault();
            return db.GetArtistById(artistId);
        });
        if (artist is null || !CanPublish()) return;

        var editDialog = new EditArtistNameDialog(artist.Id, artist.Artist);
        if (!await showEdit(editDialog)) return;
        var result = editDialog.Result;
        long? matchingArtistId = null;
        bool? preferCurrentArtistOnMerge = null;
        if (result is null && editDialog.MatchingArtist is { } matchingArtist)
        {
            if (!CanPublish()) return;
            matchingArtistId = matchingArtist.Id;
            var mergeDialog = new ArtistMergeDialog(
                artist.Id, artist.Artist, matchingArtist.Id, matchingArtist.Artist);
            if (!await showMerge(mergeDialog) || !CanPublish()) return;
            var preferredId = mergeDialog.PreferredArtistId;
            var newName = editDialog.ArtistName;
            preferCurrentArtistOnMerge = preferredId == artist.Id;
            try
            {
                result = await Task.Run(() =>
                {
                    using var db = AudioDatabase.OpenDefault();
                    return db.MergeArtists(artist.Id, matchingArtist.Id, preferredId, newName);
                });
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "Artist merge");
                if (CanPublish())
                {
                    ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistRenameFailed;
                    ArtistInfoStatusTextBlock.IsVisible = true;
                }
                return;
            }
        }
        if (result is null) return;

        // Index admitted data changes even when the originating detail was abandoned.
        _ = UpdateSearchIndexAfterArtistRenameAsync(result.ArtistId);
        var remoteRenameSucceeded = await RenameMatchingOrynivoArtistsAsync(
            artist.Artist, result.ArtistName, preferCurrentArtistOnMerge);
        if (!CanPublish()) return;
        bool Matches(long? id) => id == artistId || (matchingArtistId is long match && id == match);
        if (_currentOrynivoTrackRow is null && _currentTrackId == trackId &&
            _currentFilePath == trackPath && ReferenceEquals(_currentNowPlayingProvider, provider) &&
            Matches(_currentArtistId))
        {
            _currentArtistId = result.ArtistId;
            _currentArtistName = result.ArtistName;
            NowPlayingArtistBlock.Text = result.ArtistName;
        }
        if (Matches(_activeArtistFilterId))
        {
            _activeArtistFilterId = result.ArtistId;
            _activeArtistFilterName = result.ArtistName;
        }
        displayedId = result.ArtistId;
        displayedTitle = result.ArtistName;
        _artistInfoDisplayedId = displayedId;
        ArtistInfoTitleButton.Content = displayedTitle;
        ArtistInfoStatusTextBlock.IsVisible = false;
        await ReloadVisibleArtistListAsync(result.ArtistId, CanPublish);
        if (!CanPublish()) return;
        // Retain only the next profile admission; cancellation callbacks may replace it.
        observation = observation with { Version = unchecked(observation.Version + 1) };
        await ShowArtistInfoAsync(result.ArtistId, forceRefresh: false);
        if (CanPublish() && !remoteRenameSucceeded)
        {
            ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.OrynivoConnectionFailed;
            ArtistInfoStatusTextBlock.IsVisible = true;
        }
    }

    /// <summary>
    /// Renames every reachable Orynivo Server artist whose normalized identity matches the
    /// artist's previous local name. When the local rename required a merge, the same choice
    /// of surviving identity is applied to an equivalent collision on every server.
    /// </summary>
    /// <param name="previousArtistName">Display name used to find matching remote identities.</param>
    /// <param name="artistName">New display name to apply.</param>
    /// <param name="preferCurrentArtistOnMerge">
    /// <see langword="true"/> to retain the identity being renamed,
    /// <see langword="false"/> to retain the existing target-name identity, or
    /// <see langword="null"/> when no merge decision was made.
    /// </param>
    /// <returns><see langword="true"/> when every original source remained valid and every matching identity was renamed.</returns>
    private async Task<bool> RenameMatchingOrynivoArtistsAsync(
        string? previousArtistName,
        string artistName,
        bool? preferCurrentArtistOnMerge = null)
    {
        var comparisonKey = ArtistNameNormalizer.CreateComparisonKey(previousArtistName);
        if (comparisonKey.Length == 0)
            return true;

        // Accepted renames outlive navigation, but must never move to another connection.
        var sources = (_settings.OrynivoServers ?? []).Select(server => (
            Server: new OrynivoServerSettings
            {
                Id = server.Id, Name = server.Name, BaseUrl = server.BaseUrl,
                ApiKey = server.ApiKey, ProfileId = server.ProfileId,
                StreamingFormat = server.StreamingFormat,
                StreamingBitrateKbps = server.StreamingBitrateKbps
            },
            IsCurrent: CreateArtistProfileSourceGuard(server))).ToArray();
        var succeeded = true;
        foreach (var source in sources)
        {
            if (!source.IsCurrent())
            {
                succeeded = false;
                continue;
            }
            var server = source.Server;
            try
            {
                var artists = await _orynivoClient.GetArtistsAsync(server);
                if (!source.IsCurrent())
                {
                    succeeded = false;
                    continue;
                }
                foreach (var artist in artists.Where(candidate =>
                             ArtistNameNormalizer.CreateComparisonKey(candidate.Name) == comparisonKey))
                {
                    if (!source.IsCurrent())
                    {
                        succeeded = false;
                        break;
                    }
                    var response = await _orynivoClient.RenameArtistAsync(
                        server,
                        artist.Id,
                        artistName,
                        preferredArtistId: null);
                    if (!source.IsCurrent())
                    {
                        succeeded = false;
                        break;
                    }
                    if (response?.Result is null &&
                        response?.MatchingArtist is { } matchingArtist &&
                        preferCurrentArtistOnMerge is bool preferCurrent)
                    {
                        response = await _orynivoClient.RenameArtistAsync(
                            server,
                            artist.Id,
                            artistName,
                            preferCurrent ? artist.Id : matchingArtist.Id);
                    }
                    if (response?.Result is null || !source.IsCurrent())
                        succeeded = false;
                }
                if (source.IsCurrent()) DeleteOrynivoArtistListCache(server);
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "Unified remote artist rename");
                succeeded = false;
            }
        }

        return succeeded;
    }

    /// <summary>Shows the rename and merge dialogs for the original server artist.</summary>
    /// <param name="row">The displayed source-owned artist row.</param>
    /// <returns>A task completing accepted data work and current UI publication.</returns>
    private Task EditOrynivoArtistNameAsync(ContentRow row) =>
        EditDisplayedOrynivoArtistNameAsync(row,
            dialog => dialog.ShowDialog<bool>(this),
            dialog => dialog.ShowDialog<bool>(this));

    /// <summary>Retains server and detail identity through edit/merge admission and publication.</summary>
    /// <param name="row">The original source-owned artist row.</param>
    /// <param name="showEdit">Shows the rename confirmation.</param>
    /// <param name="showMerge">Shows the explicit merge survivor choice.</param>
    /// <returns>A task completing accepted data changes and the retained detail refresh.</returns>
    private async Task EditDisplayedOrynivoArtistNameAsync(ContentRow row,
        Func<EditArtistNameDialog, Task<bool>> showEdit,
        Func<ArtistMergeDialog, Task<bool>> showMerge)
    {
        var server = row.OrynivoServer ?? _activeOrynivoServer;
        if (server is null || row.Id is not long artistId || row.EntityType != "OrynivoArtist") return;
        var connection = new OrynivoServerSettings
        {
            Id = server.Id, Name = server.Name, BaseUrl = server.BaseUrl,
            ApiKey = server.ApiKey, ProfileId = server.ProfileId,
            StreamingFormat = server.StreamingFormat, StreamingBitrateKbps = server.StreamingBitrateKbps
        };
        var sourceIsCurrent = CreateArtistProfileSourceGuard(server);
        var previousArtistName = row.Title;
        var rowId = row.Id;
        var rowTitle = row.Title;
        var displayedRow = row;
        var displayedTitle = ArtistInfoTitleButton.Content as string;
        var displayedId = _artistInfoDisplayedId;
        var tag = _currentTopLevelTag;
        var detailVersion = _artistDetailLoadVersion;
        var unifiedName = _artistInfoUnifiedArtistName;
        var observation = _artistProfileLoads.CaptureCurrent();
        var restore = _navigationRestores.Context;
        var track = _currentOrynivoTrackRow;
        var provider = _currentNowPlayingProvider;
        bool CanPublish() => ArtistInfoView.IsVisible && !_nowPlayingRemoteArtistInfo &&
            ReferenceEquals(_artistInfoDisplayedRemoteRow, displayedRow) &&
            _artistInfoDisplayedId == displayedId && ArtistInfoTitleButton.Content as string == displayedTitle &&
            row.Id == rowId && row.Title == rowTitle && row.EntityType == "OrynivoArtist" &&
            ReferenceEquals(row.OrynivoServer ?? _activeOrynivoServer, server) && sourceIsCurrent() &&
            _currentTopLevelTag == tag && _artistDetailLoadVersion == detailVersion &&
            _artistInfoUnifiedArtistName == unifiedName && _artistProfileLoads.IsCurrent(observation) &&
            (restore is null || _navigationRestores.IsCurrent(restore));
        if (!CanPublish()) return;
        var editDialog = new EditArtistNameDialog(artistId, previousArtistName ?? string.Empty,
            (_, name) =>
            {
                if (!CanPublish()) throw new InvalidOperationException("The artist edit is no longer current.");
                return CommitOrynivoArtistRenameAsync(connection, artistId, name, preferredArtistId: null);
            });
        if (!await showEdit(editDialog)) return;
        var result = editDialog.Result;
        long? matchingArtistId = null;
        if (result is null && editDialog.MatchingArtist is { } matchingArtist)
        {
            if (!CanPublish()) return;
            matchingArtistId = matchingArtist.Id;
            var mergeDialog = new ArtistMergeDialog(
                artistId, previousArtistName ?? string.Empty, matchingArtist.Id, matchingArtist.Artist);
            if (!await showMerge(mergeDialog) || !CanPublish()) return;
            try
            {
                (result, _) = await CommitOrynivoArtistRenameAsync(
                    connection, artistId, editDialog.ArtistName, mergeDialog.PreferredArtistId);
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "Remote artist merge");
                if (CanPublish())
                {
                    ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.ArtistRenameFailed;
                    ArtistInfoStatusTextBlock.IsVisible = true;
                }
                return;
            }
        }
        if (result is null) return;
        // Finish accepted data work even if its originating view has been abandoned.
        InvalidateUnifiedLibraryViewCache();
        if (sourceIsCurrent()) DeleteOrynivoArtistListCache(connection);
        var localRenameSucceeded = true;
        try
        {
            localRenameSucceeded = await RenameMatchingLocalArtistAsync(previousArtistName, result.ArtistName);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "Remote artist local rename synchronization");
            localRenameSucceeded = false;
        }
        var otherRemoteRenamesSucceeded = sourceIsCurrent() &&
            await RenameMatchingOrynivoArtistsAsync(previousArtistName, result.ArtistName);
        if (!CanPublish()) return;
        rowTitle = result.ArtistName;
        row.ArtistId = result.ArtistId;
        row.Title = rowTitle;
        displayedTitle = result.ArtistName;
        ArtistInfoTitleButton.Content = displayedTitle;
        ArtistInfoStatusTextBlock.IsVisible = false;
        if (track is not null && ReferenceEquals(_currentOrynivoTrackRow, track) &&
            ReferenceEquals(_currentNowPlayingProvider, provider) &&
            track.OrynivoServer is { } trackServer && trackServer.Id == connection.Id &&
            trackServer.Name == connection.Name && trackServer.BaseUrl == connection.BaseUrl &&
            trackServer.ApiKey == connection.ApiKey && trackServer.ProfileId == connection.ProfileId &&
            (track.ArtistId == artistId || (matchingArtistId is long match && track.ArtistId == match)))
        {
            track.ArtistId = result.ArtistId;
            NowPlayingArtistBlock.Text = result.ArtistName;
        }
        var refreshed = await _orynivoClient.GetArtistAsync(connection, result.ArtistId);
        if (!CanPublish()) return;
        var detailRow = refreshed is not null ? ToOrynivoArtistContentRow(server, refreshed) : new ContentRow
        {
            Id = result.ArtistId, ArtistId = result.ArtistId, Title = result.ArtistName,
            EntityType = "OrynivoArtist", ExternalId = result.ArtistId.ToString(CultureInfo.InvariantCulture),
            OrynivoServer = server, FilePath = string.Empty
        };
        displayedRow = detailRow;
        observation = observation with { Version = unchecked(observation.Version + 1) };
        await ShowOrynivoArtistInfoAsync(detailRow, forceRefresh: false);
        if (CanPublish() && (!localRenameSucceeded || !otherRemoteRenamesSucceeded))
        {
            ArtistInfoStatusTextBlock.Text = LocalizationManager.Current.OrynivoConnectionFailed;
            ArtistInfoStatusTextBlock.IsVisible = true;
        }
    }

    /// <summary>
    /// Renames the local artist whose normalized identity matches a remotely renamed artist.
    /// A target-name collision is left unresolved because a merge requires an explicit choice.
    /// </summary>
    /// <param name="previousArtistName">Display name used to find the local identity.</param>
    /// <param name="artistName">New display name to apply.</param>
    /// <returns><see langword="true"/> when no local match exists or the match was renamed.</returns>
    private static Task<bool> RenameMatchingLocalArtistAsync(
        string? previousArtistName,
        string artistName) => Task.Run(() =>
    {
        var comparisonKey = ArtistNameNormalizer.CreateComparisonKey(previousArtistName);
        if (comparisonKey.Length == 0)
            return true;

        using var db = AudioDatabase.OpenDefault();
        var localArtist = db.GetArtistsLite().FirstOrDefault(candidate =>
            ArtistNameNormalizer.CreateComparisonKey(candidate.Artist) == comparisonKey);
        if (localArtist is null)
            return true;
        if (db.FindArtistByName(artistName, localArtist.Id) is not null)
            return false;

        db.RenameArtist(localArtist.Id, artistName);
        TrackSearchIndex.UpdateMany(db.GetTracksForArtistSearchIndex(localArtist.Id));
        return true;
    });

    /// <summary>Commits a rename or explicit merge to the captured server connection.</summary>
    /// <param name="server">The transient connection snapshot.</param>
    /// <param name="artistId">The original server artist identity.</param>
    /// <param name="artistName">The requested canonical name.</param>
    /// <param name="preferredArtistId">The chosen survivor, or null for collision detection.</param>
    /// <returns>The accepted result or collision requiring an explicit choice.</returns>
    private async Task<(ArtistRenameResult? Result, ArtistInfo? MatchingArtist)> CommitOrynivoArtistRenameAsync(
        OrynivoServerSettings server,
        long artistId,
        string artistName,
        long? preferredArtistId)
    {
        var response = await _orynivoClient.RenameArtistAsync(
            server,
            artistId,
            artistName,
            preferredArtistId);
        if (response is null)
            throw new InvalidOperationException("The remote artist could not be renamed.");

        return (response.Result, response.MatchingArtist is null ? null : ToArtistInfo(response.MatchingArtist));
    }
}
