using Orynivo.Library;
using Xunit;

namespace Orynivo.Core.Tests;

/// <summary>Checks active-profile personal state across every compact track materialization path.</summary>
public sealed class CompactTrackProfileTests
{
    /// <summary>Explicit false and zero values override legacy state while absent profile state retains compatibility fallback.</summary>
    /// <param name="profile">Profile to read.</param>
    /// <param name="favorite">Expected favorite state.</param>
    /// <param name="rating">Expected personal rating.</param>
    [Theory]
    [InlineData("standard", true, 3)]
    [InlineData("alpha", false, 5)]
    [InlineData("beta", true, 0)]
    [InlineData("uninitialized", true, 3)]
    public void AllCompactReads_ApplyPersonalState(string profile, bool favorite, int rating)
    {
        using var fixture = CoreTestDatabase.Create("orynivo-compact-profile");
        using var db = fixture.Open();
        var previous = AudioDatabase.ActiveProfileId;
        try
        {
            AudioDatabase.SetActiveProfile("standard");
            var path = fixture.PathOf("synthetic.flac");
            db.Upsert(new TrackRecord
            {
                Path = path,
                FileName = "synthetic.flac",
                Title = "Synthetic track",
                Artist = "Synthetic artist",
                AlbumArtist = "Synthetic artist",
                Album = "Synthetic album",
                Format = "flac"
            });
            var id = db.GetTrackIdByPath(path)!.Value;
            db.SetTrackFavorite(id, true); db.SetTrackUserRating(id, 3);
            AudioDatabase.SetActiveProfile("alpha");
            db.SetTrackFavorite(id, false); db.SetTrackUserRating(id, 5);
            AudioDatabase.SetActiveProfile("beta");
            db.SetTrackFavorite(id, true); db.SetTrackUserRating(id, 0);
            AudioDatabase.SetActiveProfile(profile);
            var album = db.GetAlbumsLite().Single().Id;
            var artist = db.GetArtistsLite().Single().Id;
            var reads = new[] { db.GetTrackList(), db.GetTrackListByAlbum(album),
                db.GetTrackListByAlbum(album, artist), db.GetTrackListByPaths([path, path, fixture.PathOf("missing.flac")]),
                db.GetTrackListByIds([id, id, long.MaxValue]), db.GetTrackListPage(0, 1) };
            foreach (var rows in reads)
            {
                var track = Assert.Single(rows);
                Assert.Equal(id, track.Id);
                Assert.Equal(favorite, track.IsFavorite);
                Assert.Equal(rating, track.UserRating);
                Assert.Equal("Synthetic track", track.Title);
            }
            Assert.Empty(db.GetTrackListByAlbum(album, long.MaxValue));
        }
        finally { AudioDatabase.SetActiveProfile(previous); }
    }
}
