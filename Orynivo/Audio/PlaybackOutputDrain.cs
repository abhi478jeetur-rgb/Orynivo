namespace Orynivo.Audio;

/// <summary>Combines preparation completion with detached-player retirement without owning active output or UI state.</summary>
internal static class PlaybackOutputDrain
{
    /// <summary>Registers retirement immediately and optionally waits for preparation and late-created output cleanup.</summary>
    /// <param name="player">Uniquely detached player, or null if already detached.</param>
    /// <param name="waitForCompleteDisposal">Whether to drain preparations and all registered retirements instead of a bounded transition.</param>
    /// <param name="retirements">Existing shared detached-player owner.</param>
    /// <param name="preparations">Dispatcher-owned preparations, already cancelled by the caller when stopping.</param>
    /// <returns>A bounded retirement wait, or complete release including late preparation results.</returns>
    internal static async Task RetireAsync(
        IAudioPlayer? player, bool waitForCompleteDisposal,
        AudioPlayerRetirement retirements, PlaybackPreparationTracker preparations)
    {
        var retirement = retirements.RetireAsync(player, waitForCompleteDisposal);
        if (waitForCompleteDisposal)
            await preparations.DrainAsync();
        await retirement;
        if (waitForCompleteDisposal)
            await retirements.RetireAsync(null, waitForCompleteDisposal: true);
    }
}
