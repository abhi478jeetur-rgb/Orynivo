namespace Orynivo;

/// <summary>Chooses a selected row occurrence without collapsing duplicate playlist paths.</summary>
internal static class PlaylistRowPlaybackIndex
{
    /// <summary>Finds the exact playable occurrence, falling back to a matching path for detached rows.</summary>
    /// <param name="selected">Selected row.</param>
    /// <param name="playableRows">Ordered rows after non-playable placeholders have been removed.</param>
    /// <returns>The playable index, or minus one when the selected row cannot be played.</returns>
    internal static int Find(ContentRow selected, IReadOnlyList<ContentRow> playableRows)
    {
        if (string.IsNullOrEmpty(selected.FilePath)) return -1;
        for (var index = 0; index < playableRows.Count; index++)
            if (ReferenceEquals(playableRows[index], selected)) return index;
        for (var index = 0; index < playableRows.Count; index++)
            if (playableRows[index].FilePath == selected.FilePath) return index;
        return -1;
    }
}
