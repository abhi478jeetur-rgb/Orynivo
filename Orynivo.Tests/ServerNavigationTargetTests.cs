using Xunit;

namespace Orynivo.Tests;

/// <summary>Protects credential-free server routing and album source ownership without a window.</summary>
public sealed class ServerNavigationTargetTests
{
    /// <summary>Non-server and malformed tags cannot resolve against ambient server state.</summary>
    /// <param name="tag">Invalid navigation identity.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Tracks")]
    [InlineData("PlexLibrary:fixture:1")]
    [InlineData("OrynivoServerPlaylist:fixture:1")]
    [InlineData("orynivoserver:fixture:Albums")]
    [InlineData("OrynivoServer:")]
    [InlineData("OrynivoServer: :Albums")]
    [InlineData("OrynivoServer::Albums")]
    public void InvalidTagsHaveNoTarget(string? tag)
    {
        Assert.False(ServerNavigationTarget.TryParse(tag, out var target));
        Assert.Null(target);
    }

    /// <summary>The parser preserves legacy Root defaults and existing view grammar.</summary>
    /// <param name="tag">Existing server tag.</param>
    /// <param name="view">Expected parsed view.</param>
    [Theory]
    [InlineData("OrynivoServer:fixture", "Root")]
    [InlineData("OrynivoServer:fixture:", "Root")]
    [InlineData("OrynivoServer:fixture: ", "Root")]
    [InlineData("OrynivoServer:fixture:Artists", "Artists")]
    [InlineData("OrynivoServer:fixture:Albums", "Albums")]
    [InlineData("OrynivoServer:fixture:Tracks", "Tracks")]
    [InlineData("OrynivoServer:fixture:Folders", "Folders")]
    [InlineData("OrynivoServer:fixture:Legacy:Detail", "Legacy:Detail")]
    public void ExistingTagsRetainTheirServerAndView(string tag, string view)
    {
        Assert.True(ServerNavigationTarget.TryParse(tag, out var target));
        Assert.Equal("fixture", target!.ServerId);
        Assert.Equal(view, target.View);
        Assert.Equal($"OrynivoServer:fixture:{view}", target.Tag);
    }

    /// <summary>Album capture cannot inherit a foreign server's tag, even for matching numeric IDs.</summary>
    /// <param name="origin">Sidebar origin unrelated to the owning source.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("Dashboard")]
    [InlineData("OrynivoServer:other:Albums")]
    [InlineData("OrynivoServer:FIXTURE:Albums")]
    [InlineData("OrynivoServer::Albums")]
    public void AlbumTargetRepairsForeignOrInvalidOrigin(string? origin)
    {
        Assert.Equal("OrynivoServer:fixture:Albums",
            ServerNavigationTarget.ForAlbum("fixture", origin));
        var capture = NavigationCaptureDecision.Select(new(false, false, origin, 42, null,
            null, false, true, "fixture", false));
        Assert.Equal(NavigationCaptureKind.ServerAlbum, capture.Kind);
        Assert.Equal("OrynivoServer:fixture:Albums", capture.NavigationTag);
    }

    /// <summary>An origin from the owning source keeps its existing view.</summary>
    [Fact]
    public void AlbumTargetRetainsMatchingOrigin()
    {
        Assert.Equal("OrynivoServer:fixture:Tracks",
            ServerNavigationTarget.ForAlbum("fixture", "OrynivoServer:fixture:Tracks"));
        Assert.Equal("OrynivoServer:fixture", ServerNavigationTarget.ForAlbum("fixture", "OrynivoServer:fixture"));
        Assert.Equal("OrynivoServer:fixture:", ServerNavigationTarget.ForAlbum("fixture", "OrynivoServer:fixture:"));
    }
}
