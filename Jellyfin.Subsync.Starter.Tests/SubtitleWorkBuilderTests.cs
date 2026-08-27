using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests;

/// <summary>
/// <see cref="SubtitleWorkBuilder.BuildWork"/> replaced a positional regex
/// that re-derived the video-to-subtitle association from filenames. That
/// regex silently mis-grouped ".forced", ".sdh" and "pt-BR" tagged files -
/// exactly what Bazarr and subliminal produce - and the subtitle was then
/// never synced, with no error anywhere. Association is now Jellyfin's:
/// the streams handed in here are already resolved, so the tests below pin
/// down what the plugin still decides for itself - which streams it will
/// touch, and in what order.
/// </summary>
public class SubtitleWorkBuilderTests
{
    private static PluginConfiguration DefaultConfig()
        => new() { SubtitleExtensions = ["srt", "ass", "ssa", "vtt", "sub"] };

    private static MediaStream External(string path, int index = 0)
        => new()
        {
            Type = MediaStreamType.Subtitle,
            IsExternal = true,
            Path = path,
            Index = index
        };

    private static ItemSubtitleWork Build(string? itemPath, params MediaStream[] streams)
        => SubtitleWorkBuilder.BuildWork(itemPath, isDiscImageOrFolder: false, streams, DefaultConfig());

    // --- A. The cases the old regex got wrong -------------------------------

    /// <summary>
    /// Every row here was silently never synced (or synced against a video
    /// that doesn't exist) under the old GetBaseName regex. The builder
    /// doesn't parse the filename at all, so all of them now work.
    /// </summary>
    [Theory]
    // Derived "Show.S01" - the ".E02" looked like a language tag.
    [InlineData("/m/Show.S01.E02.mkv", "/m/Show.S01.E02.srt")]
    // Derived "Movie" - "4K" looked like a language tag.
    [InlineData("/m/Movie.4K.mkv", "/m/Movie.4K.srt")]
    // Derived "Movie.Part" - "II" looked like a language tag.
    [InlineData("/m/Movie.Part.II.mkv", "/m/Movie.Part.II.srt")]
    // Derived "Movie.en" - only the last tag was stripped.
    [InlineData("/m/Movie.mkv", "/m/Movie.en.sdh.srt")]
    // Derived "Movie.pt-BR" - the hyphen broke the \w{2,3} match.
    [InlineData("/m/Movie.mkv", "/m/Movie.pt-BR.srt")]
    // Derived "Movie.forced" - "forced" is six characters, not 2-3.
    [InlineData("/m/Movie.mkv", "/m/Movie.forced.srt")]
    public void NamingIsIrrelevant_JellyfinsAssociationIsTrusted(string videoPath, string subtitlePath)
    {
        var work = Build(videoPath, External(subtitlePath));

        Assert.Equal(ItemSkipReason.None, work.Reason);
        Assert.NotNull(work.Group);
        Assert.Equal(videoPath, work.Group.VideoPath);
        Assert.Equal([subtitlePath], work.Group.SubtitlePaths);
    }

    /// <summary>
    /// The old regex bucketed these into four separate groups, so each one
    /// paid for a full video sync. As one group, the first syncs against the
    /// video and the other three sync against it instead - much cheaper.
    /// The flags themselves are Jellyfin's business, not the builder's.
    /// </summary>
    [Fact]
    public void TaggedVariantsOfOneVideo_FormASingleGroup()
    {
        var english = External("/m/Movie.en.srt", 0);
        english.Language = "eng";

        var forced = External("/m/Movie.forced.srt", 1);
        forced.IsForced = true;

        var sdh = External("/m/Movie.en.sdh.srt", 2);
        sdh.IsHearingImpaired = true;

        var brazilian = External("/m/Movie.pt-BR.srt", 3);
        brazilian.Language = "por";

        var work = Build("/m/Movie.mkv", english, forced, sdh, brazilian);

        Assert.NotNull(work.Group);
        Assert.Equal(
            ["/m/Movie.en.srt", "/m/Movie.forced.srt", "/m/Movie.en.sdh.srt", "/m/Movie.pt-BR.srt"],
            work.Group.SubtitlePaths);
        Assert.Equal(
            ["/m/Movie.forced.srt"],
            work.Group.ForcedSubtitlePaths);
    }

    // --- B. Stream filtering ------------------------------------------------

    [Fact]
    public void EmbeddedStream_IsIgnored()
    {
        var embedded = External("/m/Movie.mkv", 0);
        embedded.IsExternal = false;

        var work = Build("/m/Movie.mkv", embedded);

        Assert.Equal(ItemSkipReason.NoUsableSubtitles, work.Reason);
        Assert.Null(work.Group);
    }

    [Fact]
    public void NonSubtitleStream_IsIgnored()
    {
        var audio = External("/m/Movie.en.srt", 0);
        audio.Type = MediaStreamType.Audio;

        Assert.Null(Build("/m/Movie.mkv", audio).Group);
    }

    [Fact]
    public void ExternalUrlStream_IsIgnored()
    {
        var remote = External("/m/Movie.en.srt", 0);
        remote.IsExternalUrl = true;

        Assert.Null(Build("/m/Movie.mkv", remote).Group);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyStreamPath_IsIgnored(string? path)
    {
        Assert.Null(Build("/m/Movie.mkv", External(path!)).Group);
    }

    /// <summary>
    /// Jellyfin reports the same external file once per media source, so an
    /// item with alternate versions can hand us duplicates. Syncing the same
    /// file twice in one run would sync it against its own output.
    /// </summary>
    [Fact]
    public void DuplicateStreamPaths_AreDeduplicated()
    {
        var work = Build("/m/Movie.mkv", External("/m/Movie.en.srt", 0), External("/m/Movie.en.srt", 1));

        Assert.NotNull(work.Group);
        Assert.Equal(["/m/Movie.en.srt"], work.Group.SubtitlePaths);
    }

    // --- C. The sidecar's own byproducts ------------------------------------

    /// <summary>
    /// Jellyfin indexes "Movie.en_original_backup.srt" as an external
    /// subtitle of "Movie.mkv" - the prefix and the "." delimiter match, and
    /// the rest is just flags it can't parse. Without this filter the plugin
    /// would feed its own output back in as work on every sweep.
    /// </summary>
    [Theory]
    [InlineData("/m/Movie_synced_temp.srt")]
    [InlineData("/m/Movie_original_backup.srt")]
    [InlineData("/m/Movie.en_original_backup.srt")]
    [InlineData("/m/Movie.en_synced_temp.srt")]
    [InlineData("/m/MOVIE.EN_ORIGINAL_BACKUP.SRT")]
    public void SidecarByproduct_IsIgnored(string path)
    {
        Assert.Null(Build("/m/Movie.mkv", External(path)).Group);
    }

    [Fact]
    public void Byproduct_IsIgnoredButRealSiblingSurvives()
    {
        var work = Build(
            "/m/Movie.mkv",
            External("/m/Movie.en.srt", 0),
            External("/m/Movie.en_original_backup.srt", 1));

        Assert.NotNull(work.Group);
        Assert.Equal(["/m/Movie.en.srt"], work.Group.SubtitlePaths);
    }

    // --- D. Extension gating ------------------------------------------------

    [Theory]
    [InlineData("/m/Movie.en.srt")]
    [InlineData("/m/Movie.en.ass")]
    [InlineData("/m/Movie.en.ssa")]
    [InlineData("/m/Movie.en.vtt")]
    [InlineData("/m/Movie.en.sub")]
    [InlineData("/m/Movie.en.SRT")]
    public void ConfiguredExtension_IsKept(string path)
    {
        Assert.NotNull(Build("/m/Movie.mkv", External(path)).Group);
    }

    /// <summary>
    /// These are all in Jellyfin's own SubtitleFileExtensions, so they
    /// genuinely arrive here - but they're not in the plugin's defaults and
    /// ffsubsync can't align them.
    /// </summary>
    [Theory]
    [InlineData("/m/Movie.en.sup")]
    [InlineData("/m/Movie.en.mks")]
    [InlineData("/m/Movie.en.smi")]
    [InlineData("/m/Movie.en.sami")]
    public void ExtensionOutsideConfiguredList_IsIgnored(string path)
    {
        Assert.Null(Build("/m/Movie.mkv", External(path)).Group);
    }

    [Fact]
    public void CustomSubtitleExtensions_AreHonoured()
    {
        var config = new PluginConfiguration { SubtitleExtensions = ["sup"] };

        var work = SubtitleWorkBuilder.BuildWork(
            "/m/Movie.mkv",
            isDiscImageOrFolder: false,
            [External("/m/Movie.en.srt", 0), External("/m/Movie.en.sup", 1)],
            config);

        Assert.NotNull(work.Group);
        Assert.Equal(["/m/Movie.en.sup"], work.Group.SubtitlePaths);
    }

    // --- E. The single-folder sidecar contract ------------------------------

    /// <summary>
    /// The sidecar's /sync takes one folder plus two filenames, so a
    /// subtitle that doesn't sit next to its video can't be expressed. This
    /// is reported rather than dropped silently, because it's the one skip
    /// a user can actually act on.
    /// </summary>
    [Fact]
    public void SubtitleInAnotherDirectory_IsNotGroupedAndIsReported()
    {
        var work = Build("/m/Movie (2019)/Movie.mkv", External("/config/metadata/library/aa/Movie.en.srt"));

        Assert.Null(work.Group);
        Assert.Equal(ItemSkipReason.NoUsableSubtitles, work.Reason);
        Assert.Equal(["/config/metadata/library/aa/Movie.en.srt"], work.SubtitlesInOtherDirectories);
    }

    [Fact]
    public void MixedDirectories_KeepsTheBesideOnesAndReportsTheRest()
    {
        var work = Build(
            "/m/Movie (2019)/Movie.mkv",
            External("/m/Movie (2019)/Movie.en.srt", 0),
            External("/config/metadata/Movie.fr.srt", 1));

        Assert.NotNull(work.Group);
        Assert.Equal(["/m/Movie (2019)/Movie.en.srt"], work.Group.SubtitlePaths);
        Assert.Equal(["/config/metadata/Movie.fr.srt"], work.SubtitlesInOtherDirectories);
    }

    [Fact]
    public void DirectoryComparisonIsOrdinal()
    {
        var work = Build("/m/Movie/Movie.mkv", External("/M/Movie/Movie.en.srt"));

        Assert.Null(work.Group);
        Assert.Single(work.SubtitlesInOtherDirectories);
    }

    // --- F. Item-level rejections -------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyItemPath_ProducesNoPath(string? itemPath)
    {
        var work = Build(itemPath, External("/m/Movie.en.srt"));

        Assert.Equal(ItemSkipReason.NoPath, work.Reason);
        Assert.Null(work.Group);
    }

    [Fact]
    public void ItemPathWithNoParentDirectory_ProducesNoPath()
    {
        var work = Build("Movie.mkv", External("/m/Movie.en.srt"));

        Assert.Equal(ItemSkipReason.NoPath, work.Reason);
    }

    /// <summary>
    /// An ISO or BDMV/VIDEO_TS rip has no single elementary video file for
    /// ffsubsync to align against, so its subtitles are skipped even though
    /// they'd otherwise be perfectly usable.
    /// </summary>
    [Fact]
    public void DiscImageOrFolder_ProducesPathIsDiscImageOrFolder()
    {
        var work = SubtitleWorkBuilder.BuildWork(
            "/m/Movie/Movie.iso",
            isDiscImageOrFolder: true,
            [External("/m/Movie/Movie.en.srt")],
            DefaultConfig());

        Assert.Equal(ItemSkipReason.PathIsDiscImageOrFolder, work.Reason);
        Assert.Null(work.Group);
    }

    [Fact]
    public void NoStreams_ProducesNoUsableSubtitles()
    {
        var work = Build("/m/Movie.mkv");

        Assert.Equal(ItemSkipReason.NoUsableSubtitles, work.Reason);
        Assert.Null(work.Group);
        Assert.Empty(work.SubtitlesInOtherDirectories);
    }

    // --- G. Ordering ---------------------------------------------------------

    /// <summary>
    /// Whichever subtitle comes first pays for the expensive sync against
    /// the video, and the rest align against it. Deterministic ordering
    /// keeps that reproducible run to run.
    /// </summary>
    [Fact]
    public void Ordering_FollowsStreamIndexThenPath()
    {
        var work = Build(
            "/m/Movie.mkv",
            External("/m/Movie.fr.srt", 5),
            External("/m/Movie.b.srt", 2),
            External("/m/Movie.a.srt", 2),
            External("/m/Movie.en.srt", 1));

        Assert.NotNull(work.Group);
        Assert.Equal(
            ["/m/Movie.en.srt", "/m/Movie.a.srt", "/m/Movie.b.srt", "/m/Movie.fr.srt"],
            work.Group.SubtitlePaths);
    }
}

/// <summary>
/// <see cref="SubtitleWorkBuilder.ChooseReference"/> decides what a subtitle
/// is aligned against. Picking an already-synced sibling skips audio
/// extraction entirely, so getting this wrong is a large performance
/// regression rather than a correctness one - except for the self-reference
/// case, which would align a file against itself.
/// </summary>
public class ChooseReferenceTests
{
    private static readonly SubtitleSyncGroup Group = new(
        "/m/Movie.mkv",
        ["/m/Movie.en.srt", "/m/Movie.en.srt", "/m/Movie.fr.srt", "/m/Movie.de.srt"]);

    [Fact]
    public void NoSiblingSynced_UsesTheVideo()
    {
        Assert.Equal("/m/Movie.mkv", SubtitleWorkBuilder.ChooseReference("/m/Movie.en.srt", Group, _ => false));
    }

    [Fact]
    public void OneSiblingSynced_UsesThatSibling()
    {
        var reference = SubtitleWorkBuilder.ChooseReference(
            "/m/Movie.de.srt", Group, path => path == "/m/Movie.fr.srt");

        Assert.Equal("/m/Movie.fr.srt", reference);
    }

    [Fact]
    public void SeveralSiblingsSynced_UsesTheFirstInGroupOrder()
    {
        var reference = SubtitleWorkBuilder.ChooseReference(
            "/m/Movie.de.srt", Group, path => path != "/m/Movie.de.srt");

        Assert.Equal("/m/Movie.en.srt", reference);
    }

    [Fact]
    public void SubtitleIsNeverItsOwnReference()
    {
        var reference = SubtitleWorkBuilder.ChooseReference("/m/Movie.en.srt", Group, _ => true);

        Assert.NotEqual("/m/Movie.en.srt", reference);
        Assert.Equal("/m/Movie.fr.srt", reference);
    }

    [Fact]
    public void ForcedSubtitleIsNeverReference()
    {
        var reference = SubtitleWorkBuilder.ChooseReference(
            "/m/Movie.de.srt",
            new(
                "/m/Movie.mkv",
                ["/m/Movie.en.srt", "/m/Movie.en.srt", "/m/Movie.fr.srt", "/m/Movie.de.srt"],
                new HashSet<string>() {"/m/Movie.en.srt"}),
            _ => true);

        Assert.NotEqual("/m/Movie.en.srt", reference);
        Assert.Equal("/m/Movie.fr.srt", reference);
    }

    [Fact]
    public void OnlyForcedIsSynced_UsesTheVideo()
    {
        var reference = SubtitleWorkBuilder.ChooseReference(
            "/m/Movie.de.srt",
            new(
                "/m/Movie.mkv",
                ["/m/Movie.en.srt"],
                new HashSet<string>() {"/m/Movie.en.srt"}),
            _ => true);

        Assert.Equal("/m/Movie.mkv", reference);
    }

    [Fact]
    public void SingleSubtitleGroup_UsesTheVideo()
    {
        var group = new SubtitleSyncGroup("/m/Movie.mkv", ["/m/Movie.en.srt"]);

        Assert.Equal("/m/Movie.mkv", SubtitleWorkBuilder.ChooseReference("/m/Movie.en.srt", group, _ => true));
    }
}