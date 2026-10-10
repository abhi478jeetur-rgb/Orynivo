namespace Orynivo.Audio;

/// <summary>Creates a player through the window's backend adapter or an isolated test factory.</summary>
/// <param name="filePath">The resolved playable path.</param>
/// <param name="allowQueuedTracks">Whether the normal gapless queue may be included.</param>
/// <param name="cancellationToken">The owning playback cancellation token.</param>
/// <returns>The player and format, or null for incomplete output configuration.</returns>
internal delegate Task<(IAudioPlayer Player, AudioFileInfo Info)?> PlaybackPlayerFactory(
    string filePath, bool allowQueuedTracks, CancellationToken cancellationToken);
