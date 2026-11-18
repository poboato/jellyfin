using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using MediaBrowser.Providers.MediaInfo;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo;

public class MediaStreamIndexMapTests
{
    private const string SidecarEn = "/media/show/S01E01.en.srt";
    private const string SidecarDe = "/media/show/S01E01.de.srt";

    private static MediaStream Video(int index) => new() { Index = index, Type = MediaStreamType.Video, Codec = "h264" };

    private static MediaStream Audio(int index, string language) => new() { Index = index, Type = MediaStreamType.Audio, Codec = "aac", Language = language };

    private static MediaStream Subtitle(int index, string language) => new() { Index = index, Type = MediaStreamType.Subtitle, Codec = "ass", Language = language };

    private static MediaStream External(int index, string path, string language) => new()
    {
        Index = index,
        Type = MediaStreamType.Subtitle,
        Codec = "srt",
        Language = language,
        IsExternal = true,
        Path = path
    };

    [Fact]
    public void Build_SidecarAppendedAfterEmbeddedStreams_NothingMoves()
    {
        var before = new[] { Video(0), Audio(1, "jpn"), Audio(2, "eng"), Subtitle(3, "eng") };
        var after = new[] { Video(0), Audio(1, "jpn"), Audio(2, "eng"), Subtitle(3, "eng"), External(4, SidecarEn, "eng") };

        Assert.Empty(MediaStreamIndexMap.Build(before, after));
    }

    [Fact]
    public void Build_OldExternalFirstLayout_FollowsEveryStreamToItsNewIndex()
    {
        // The layout stored before this change, and the same streams with externals moved last.
        var before = new[] { External(0, SidecarEn, "eng"), Video(1), Audio(2, "jpn"), Audio(3, "eng"), Subtitle(4, "eng") };
        var after = new[] { Video(0), Audio(1, "jpn"), Audio(2, "eng"), Subtitle(3, "eng"), External(4, SidecarEn, "eng") };

        var map = MediaStreamIndexMap.Build(before, after);

        Assert.Equal(
            new Dictionary<int, int?> { [0] = 4, [1] = 0, [2] = 1, [3] = 2, [4] = 3 },
            map);
    }

    [Fact]
    public void Build_SidecarRemoved_LaterSidecarFollowedAndRemovedOneDropped()
    {
        var before = new[] { Video(0), Audio(1, "eng"), External(2, SidecarDe, "ger"), External(3, SidecarEn, "eng") };
        var after = new[] { Video(0), Audio(1, "eng"), External(2, SidecarEn, "eng") };

        var map = MediaStreamIndexMap.Build(before, after);

        Assert.Equal(new Dictionary<int, int?> { [2] = null, [3] = 2 }, map);
    }

    [Fact]
    public void Build_ReplacedFileWithDifferentTrackInSameSlot_DropsInsteadOfPointingAtIt()
    {
        var before = new[] { Video(0), Audio(1, "eng"), Audio(2, "jpn") };
        var after = new[] { Video(0), Audio(1, "jpn"), Audio(2, "eng") };

        var map = MediaStreamIndexMap.Build(before, after);

        Assert.Equal(new Dictionary<int, int?> { [1] = null, [2] = null }, map);
    }

    [Fact]
    public void Build_EmbeddedImageSubtitleFilteredOut_FullAndSignsTracksKeepTheirIdentity()
    {
        // Indexes are assigned before the library's embedded-subtitle filter drops a track, so the two
        // look-alike English tracks keep their indexes and must not be swapped for each other.
        // Only the dropped image track is gone.
        var before = new[] { Video(0), Audio(1, "jpn"), new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "PGSSUB", Language = "eng" }, Subtitle(3, "eng"), Subtitle(4, "eng") };
        var after = new[] { Video(0), Audio(1, "jpn"), Subtitle(3, "eng"), Subtitle(4, "eng") };

        Assert.Equal(new Dictionary<int, int?> { [2] = null }, MediaStreamIndexMap.Build(before, after));
    }

    [Fact]
    public void Build_SameLayout_IsEmpty()
    {
        var streams = new[] { Video(0), Audio(1, "jpn"), External(2, SidecarEn, "eng") };
        var again = new[] { Video(0), Audio(1, "jpn"), External(2, SidecarEn, "eng") };

        Assert.Empty(MediaStreamIndexMap.Build(streams, again));
    }
}
