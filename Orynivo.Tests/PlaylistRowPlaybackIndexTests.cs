using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies selected playlist occurrences independently of audio devices.</summary>
public sealed class PlaylistRowPlaybackIndexTests
{
    /// <summary>A second occurrence starts at its own index after unresolved rows are excluded.</summary>
    [Fact]
    public void DuplicatePath_PreservesSelectedOccurrence()
    {
        var first = new ContentRow { FilePath = "fixture.flac", PlaylistEntryId = 1 };
        var missing = new ContentRow { EntityType = "MissingPlaylistTrack", PlaylistEntryId = 2 };
        var second = new ContentRow { FilePath = "fixture.flac", PlaylistEntryId = 3 };
        var playable = new[] { first, missing, second }.Where(row => row.FilePath.Length > 0).ToArray();
        Assert.Equal(1, PlaylistRowPlaybackIndex.Find(second, playable));
        Assert.Equal(-1, PlaylistRowPlaybackIndex.Find(missing, playable));
    }

    /// <summary>Detached selections can match a path while absent paths never replace the queue.</summary>
    [Fact]
    public void DetachedSelection_UsesPathFallbackOrRejects()
    {
        var rows = new[] { new ContentRow { FilePath = "fixture.flac" } };
        Assert.Equal(0, PlaylistRowPlaybackIndex.Find(new ContentRow { FilePath = "fixture.flac" }, rows));
        Assert.Equal(-1, PlaylistRowPlaybackIndex.Find(new ContentRow { FilePath = "absent.flac" }, rows));
    }
}
