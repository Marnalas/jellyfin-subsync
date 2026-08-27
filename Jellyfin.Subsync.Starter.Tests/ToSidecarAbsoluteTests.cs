using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Infrastructure;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests;

/// <summary>
/// <see cref="SubtitleMatcher.ToSidecarAbsolute"/> re-roots a Jellyfin-side
/// path onto the sidecar's view of the same files. A wrong answer here is
/// handed straight to the sidecar, which then fails the job on a path that
/// doesn't exist (or, worse, resolves to a different library's file), so
/// the root-matching rules are pinned down case by case.
/// </summary>
public class ToSidecarAbsoluteTests
{
    private static PluginConfiguration ConfigWith(params (string Jellyfin, string Sidecar)[] maps)
        => new()
        {
            WatchedPathsMaps = [.. maps.Select(map => new PathMapEntry
            {
                JellyfinPath = map.Jellyfin,
                SidecarPath = map.Sidecar
            })]
        };

    private static readonly (string Jellyfin, string Sidecar)[] SingleMap =
        [("/media/Movies", "/mnt/media/Movies")];

    [Fact]
    public void FileDirectlyAtMappedRoot_ReRootsFolderAndKeepsFilename()
    {
        var result = SubtitleMatcher.ToSidecarAbsolute("/media/Movies/Movie.en.srt", ConfigWith(SingleMap));

        Assert.Equal(("/mnt/media/Movies", "Movie.en.srt"), result);
    }

    [Theory]
    [InlineData("/media/Movies/Movie (2019)/Movie.en.srt", "/mnt/media/Movies/Movie (2019)", "Movie.en.srt")]
    [InlineData("/media/Movies/A/B/C/Movie.srt", "/mnt/media/Movies/A/B/C", "Movie.srt")]
    public void NestedFile_KeepsRelativeSubdirectories(string input, string expectedFolder, string expectedFile)
    {
        var result = SubtitleMatcher.ToSidecarAbsolute(input, ConfigWith(SingleMap));

        Assert.Equal((expectedFolder, expectedFile), result);
    }

    /// <summary>
    /// Overlapping roots: the deepest configured entry has to win, whichever
    /// order the entries happen to be listed in - otherwise a nested library
    /// silently resolves through its parent's mapping.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingRoots_LongestMatchWinsRegardlessOfOrder(bool nestedEntryFirst)
    {
        (string, string) parent = ("/media", "/mnt/all-media");
        (string, string) nested = ("/media/Movies", "/mnt/movies-only");
        var config = nestedEntryFirst ? ConfigWith(nested, parent) : ConfigWith(parent, nested);

        Assert.Equal(
            ("/mnt/movies-only/Movie (2019)", "Movie.en.srt"),
            SubtitleMatcher.ToSidecarAbsolute("/media/Movies/Movie (2019)/Movie.en.srt", config));

        // Anything outside the nested root still resolves through the parent.
        Assert.Equal(
            ("/mnt/all-media/Series/Show", "Show.en.srt"),
            SubtitleMatcher.ToSidecarAbsolute("/media/Series/Show/Show.en.srt", config));
    }

    /// <summary>
    /// A sibling directory that merely starts with the same characters is
    /// not a match - "/media/Movies" must not swallow "/media/Movies2".
    /// </summary>
    [Fact]
    public void SiblingRootSharingAPrefix_IsNotMatched()
    {
        var config = ConfigWith(("/media/Movies", "/mnt/media/Movies"));

        Assert.Null(SubtitleMatcher.ToSidecarAbsolute("/media/Movies2/Movie.en.srt", config));
    }

    [Fact]
    public void SiblingRootsSharingAPrefix_EachResolveToTheirOwnMapping()
    {
        var config = ConfigWith(
            ("/media/Movies", "/mnt/a"),
            ("/media/Movies2", "/mnt/b"));

        Assert.Equal(("/mnt/a", "x.srt"), SubtitleMatcher.ToSidecarAbsolute("/media/Movies/x.srt", config));
        Assert.Equal(("/mnt/b", "x.srt"), SubtitleMatcher.ToSidecarAbsolute("/media/Movies2/x.srt", config));
    }

    /// <summary>Trailing slashes are a normal thing to type into the config page.</summary>
    [Theory]
    [InlineData("/media/Movies/", "/mnt/media/Movies/")]
    [InlineData("/media/Movies", "/mnt/media/Movies/")]
    [InlineData("/media/Movies/", "/mnt/media/Movies")]
    public void TrailingSlashesInConfig_AreNormalised(string jellyfinPath, string sidecarPath)
    {
        var config = ConfigWith((jellyfinPath, sidecarPath));

        Assert.Equal(
            ("/mnt/media/Movies/Movie (2019)", "Movie.en.srt"),
            SubtitleMatcher.ToSidecarAbsolute("/media/Movies/Movie (2019)/Movie.en.srt", config));
    }

    [Fact]
    public void PathOutsideEveryMapping_ReturnsNull()
        => Assert.Null(SubtitleMatcher.ToSidecarAbsolute("/elsewhere/Movie.en.srt", ConfigWith(SingleMap)));

    [Fact]
    public void NoMappingsConfigured_ReturnsNull()
        => Assert.Null(SubtitleMatcher.ToSidecarAbsolute("/media/Movies/Movie.en.srt", ConfigWith()));

    /// <summary>
    /// Matching is ordinal: container paths are Linux paths, where
    /// "/Media" and "/media" really are different directories.
    /// </summary>
    [Theory]
    [InlineData("/MEDIA/Movies/Movie.en.srt")]
    [InlineData("/media/movies/Movie.en.srt")]
    public void CaseMismatchedRoot_ReturnsNull(string input)
        => Assert.Null(SubtitleMatcher.ToSidecarAbsolute(input, ConfigWith(SingleMap)));

    /// <summary>A path that is exactly the mapped root, with no subdirectory, must not gain a stray slash.</summary>
    [Fact]
    public void RootWithoutSubdirectory_ProducesNoTrailingSeparator()
    {
        var result = SubtitleMatcher.ToSidecarAbsolute("/media/Movies/Movie.srt", ConfigWith(SingleMap));

        Assert.Equal("/mnt/media/Movies", result!.Value.Folder);
    }

    /// <summary>
    /// KNOWN LIMITATION (review item 3.4). Roots are trimmed of trailing
    /// slashes before comparison, so "/" - and an entry left blank on the
    /// config page - both trim to the empty string, which prefix-matches
    /// every absolute path. A blank row therefore captures the whole
    /// library rather than being ignored. It only loses to a longer,
    /// genuine match, so this misfires exactly when no real entry applies.
    /// These rows document today's behaviour.
    /// </summary>
    [Theory]
    [InlineData("/", "/mnt/root", "/mnt/root/media/Movies")]
    [InlineData("", "/mnt/root", "/mnt/root/media/Movies")]
    public void KnownLimitation_EmptyOrSlashRootMatchesEverything(string jellyfinPath, string sidecarPath, string currentFolder)
    {
        var config = ConfigWith((jellyfinPath, sidecarPath));

        var result = SubtitleMatcher.ToSidecarAbsolute("/media/Movies/Movie.en.srt", config);

        Assert.Equal((currentFolder, "Movie.en.srt"), result);
    }

    /// <summary>
    /// KNOWN LIMITATION (review item 3.4). A matched entry whose SidecarPath
    /// was left blank yields a relative-looking folder instead of being
    /// rejected, which the sidecar then resolves against its own working
    /// directory.
    /// </summary>
    [Fact]
    public void KnownLimitation_BlankSidecarPath_ProducesRootRelativeFolder()
    {
        var config = ConfigWith(("/media/Movies", ""));

        var result = SubtitleMatcher.ToSidecarAbsolute("/media/Movies/Sub/Movie.en.srt", config);

        Assert.Equal(("/Sub", "Movie.en.srt"), result);
    }
}