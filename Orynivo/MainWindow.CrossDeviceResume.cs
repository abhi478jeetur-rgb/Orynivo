using System.Globalization;
using Avalonia.Controls;
using Orynivo.Library;
using Orynivo.Localization;
using Orynivo.Streaming;

namespace Orynivo;

/// <summary>
/// Cross-device resume for remote Orynivo Server tracks. The last position is
/// stored per profile and track on the owning server through the authenticated
/// position endpoint, so no credential-bearing URL is ever persisted. The pure
/// <see cref="CrossDeviceResume"/> helper decides whether a stored position is
/// worth offering here.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan RemotePositionPublishInterval = TimeSpan.FromSeconds(20);

    private DateTimeOffset _lastRemotePositionPublishedAt = DateTimeOffset.MinValue;
    private double _lastRemotePositionPublished = -1;
    private double? _pendingRemoteResumeSeconds;
    private long _remoteResumeVersion;
    private CancellationTokenSource? _remoteResumeCts;
    private Audio.IAudioPlayer? _pendingRemoteResumePlayer;
    private ContentRow? _pendingRemoteResumeRow;
    private long _pendingRemoteResumeIntent;

    /// <summary>
    /// Publishes the current position of a playing remote track to its owning server.
    /// Publishing is throttled and best effort: a failure never affects playback.
    /// </summary>
    /// <param name="position">Audible position of the current track.</param>
    private void PublishRemoteTrackPosition(TimeSpan position)
    {
        if (_currentOrynivoTrackRow is not { OrynivoServer: { } server, Id: long trackId })
        {
            _lastRemotePositionPublishedAt = DateTimeOffset.MinValue;
            _lastRemotePositionPublished = -1;
            ClearRemoteResumePrompt();
            return;
        }

        if (_player is null || position <= TimeSpan.Zero)
            return;
        if (DateTimeOffset.UtcNow - _lastRemotePositionPublishedAt < RemotePositionPublishInterval)
            return;

        var seconds = position.TotalSeconds;
        if (Math.Abs(seconds - _lastRemotePositionPublished) < 1)
            return;

        _lastRemotePositionPublishedAt = DateTimeOffset.UtcNow;
        _lastRemotePositionPublished = seconds;
        _ = _orynivoClient
            .SaveTrackPositionAsync(server, trackId, seconds)
            .ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// Offers the stored position of a remote track when another device left off
    /// meaningfully later than the position this device would start at.
    /// </summary>
    /// <param name="row">Remote track about to play.</param>
    /// <param name="localSeconds">Position this device starts at.</param>
    /// <returns>The cancellable, session-owned advisory lookup.</returns>
    private async Task OfferRemoteResumeAsync(ContentRow row, double localSeconds)
    {
        ClearRemoteResumePrompt();
        if (row is not { OrynivoServer: { } server, Id: long trackId })
            return;

        var player = _player;
        var intent = _playbackStarts.Current;
        var version = _remoteResumeVersion;
        if (player is null || !player.CanSeek || _playbackShuttingDown) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _playbackCts?.Token ?? CancellationToken.None);
        _remoteResumeCts = cancellation;
        var token = cancellation.Token;
        double? remoteSeconds;
        try
        {
            remoteSeconds = await _orynivoClient.GetTrackPositionAsync(server, trackId, token);
        }
        catch { return; }
        finally
        {
            if (ReferenceEquals(_remoteResumeCts, cancellation)) _remoteResumeCts = null;
        }
        if (token.IsCancellationRequested || _playbackShuttingDown || version != _remoteResumeVersion ||
            !_playbackStarts.IsCurrent(intent) || !ReferenceEquals(_player, player) ||
            !ReferenceEquals(_currentOrynivoTrackRow, row) || remoteSeconds is not double stored)
            return;

        var durationSeconds = player.Duration.TotalSeconds;
        if (!CrossDeviceResume.ShouldOffer(stored, durationSeconds, localSeconds))
            return;

        _pendingRemoteResumeSeconds = CrossDeviceResume.Normalize(stored, durationSeconds);
        _pendingRemoteResumePlayer = player;
        _pendingRemoteResumeRow = row;
        _pendingRemoteResumeIntent = intent;
        ResumePositionButton.IsVisible = true;
        ToolTip.SetTip(
            ResumePositionButton,
            string.Format(
                CultureInfo.CurrentCulture,
                LocalizationManager.Current.ResumeOnThisDeviceTooltip,
                FormatTime(TimeSpan.FromSeconds(_pendingRemoteResumeSeconds.Value))));
    }

    /// <summary>Cancels advisory lookup and clears the offered position and ownership.</summary>
    private void ClearRemoteResumePrompt()
    {
        ++_remoteResumeVersion;
        _remoteResumeCts?.Cancel();
        _remoteResumeCts = null;
        _pendingRemoteResumePlayer = null;
        _pendingRemoteResumeRow = null;
        _pendingRemoteResumeSeconds = null;
        ResumePositionButton.IsVisible = false;
    }

    /// <summary>Applies the offered position to its owning player.</summary>
    /// <param name="sender">Resume button.</param>
    /// <param name="e">Click event.</param>
    private async void ResumePositionButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ResumeRemotePositionAsync();

    /// <summary>Observes seek failure and rejects obsolete completion messages.</summary>
    /// <returns>The guarded seek completion.</returns>
    private async Task ResumeRemotePositionAsync()
    {
        var player = _pendingRemoteResumePlayer;
        var row = _pendingRemoteResumeRow;
        var intent = _pendingRemoteResumeIntent;
        if (_pendingRemoteResumeSeconds is not double position || player is null ||
            _playbackShuttingDown || !_playbackStarts.IsCurrent(intent) ||
            !ReferenceEquals(_player, player) || !ReferenceEquals(_currentOrynivoTrackRow, row))
        {
            ClearRemoteResumePrompt();
            return;
        }
        var target = Math.Min(position, Math.Max(0, player.Duration.TotalSeconds));
        ClearRemoteResumePrompt();
        var version = _remoteResumeVersion;
        using var preparation = _playbackPreparations.Begin();
        bool IsCurrent() => !preparation.Token.IsCancellationRequested && !_playbackShuttingDown && version == _remoteResumeVersion &&
            _playbackStarts.IsCurrent(intent) && ReferenceEquals(_player, player) &&
            ReferenceEquals(_currentOrynivoTrackRow, row);
        try { await player.SeekAsync(TimeSpan.FromSeconds(target)); }
        catch
        {
            if (IsCurrent()) StatusTextBlock.Text = LocalizationManager.Current.ResumeOnThisDeviceFailed;
            return;
        }
        if (!IsCurrent()) return;
        StatusTextBlock.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationManager.Current.ResumeOnThisDeviceHint,
            FormatTime(TimeSpan.FromSeconds(target)));
    }
}
