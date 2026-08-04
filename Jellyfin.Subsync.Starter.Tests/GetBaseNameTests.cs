using Jellyfin.Subsync.Starter.Infrastructure;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests
{
    /// <summary>
    /// <see cref="SubtitleMatcher.GetBaseName"/> decides which video a subtitle
    /// is matched against, and which subtitles are grouped together as siblings
    /// of the same video. Getting it wrong doesn't crash anything - the subtitle
    /// is just silently never synced - so every naming shape the sweep can meet
    /// is pinned down here.
    /// </summary>
    public class GetBaseNameTests
    {
        /// <summary>Plain "Movie.mkv" + "Movie.srt", with no language/track tag at all.</summary>
        [Theory]
        [InlineData("Movie.srt", "Movie")]
        [InlineData("Movie.mkv", "Movie")]
        [InlineData("Movie.ass", "Movie")]
        [InlineData("Movie.ssa", "Movie")]
        [InlineData("Movie.vtt", "Movie")]
        [InlineData("Movie.sub", "Movie")]
        public void UntaggedName_ReducesToStem(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>Two- and three-letter language tags are stripped, for every subtitle extension.</summary>
        [Theory]
        [InlineData("Movie.en.srt", "Movie")]
        [InlineData("Movie.fr.srt", "Movie")]
        [InlineData("Movie.eng.srt", "Movie")]
        [InlineData("Movie.rus.srt", "Movie")]
        [InlineData("Movie.en.ass", "Movie")]
        [InlineData("Movie.en.ssa", "Movie")]
        [InlineData("Movie.en.vtt", "Movie")]
        [InlineData("Movie.en.sub", "Movie")]
        public void LanguageTag_IsStripped(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>Jellyfin appends a numeric track index when one language has several tracks.</summary>
        [Theory]
        [InlineData("Movie.eng.2.srt", "Movie")]
        [InlineData("Movie.en.3.srt", "Movie")]
        [InlineData("Movie.fre.0.ass", "Movie")]
        public void LanguageTagWithTrackNumber_IsStripped(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>
        /// Scene-style names are full of dots; only the language tag and
        /// extension may be shaved off, never part of the title.
        /// </summary>
        [Theory]
        [InlineData("The.Matrix.1999.srt", "The.Matrix.1999")]
        [InlineData("The.Matrix.1999.en.srt", "The.Matrix.1999")]
        [InlineData("The.Matrix.1999.eng.2.srt", "The.Matrix.1999")]
        [InlineData("Show.S01E01.srt", "Show.S01E01")]
        [InlineData("Show.S01E01.en.srt", "Show.S01E01")]
        [InlineData("Movie.2019.srt", "Movie.2019")]
        [InlineData("Movie.720p.srt", "Movie.720p")]
        public void DottedTitle_KeepsTitleIntact(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>Some tools name subtitles after the full video filename, extension included.</summary>
        [Theory]
        [InlineData("Movie.mkv.srt", "Movie")]
        [InlineData("Movie.mp4.srt", "Movie")]
        public void VideoExtensionInName_IsStripped(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>Grouping is case-insensitive, so the base name is returned verbatim, not normalised.</summary>
        [Theory]
        [InlineData("MOVIE.EN.SRT", "MOVIE")]
        [InlineData("Movie.En.Srt", "Movie")]
        public void Casing_IsPreservedAndTagStillStripped(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>
        /// The sidecar's own byproducts keep the subtitle's extension, so they
        /// reach this method whenever something skips the IsSubtitleFile guard;
        /// they must not collapse onto the real subtitle's base name.
        /// </summary>
        [Theory]
        [InlineData("Movie_synced_temp.srt", "Movie_synced_temp")]
        [InlineData("Movie_original_backup.srt", "Movie_original_backup")]
        [InlineData("Movie.en_original_backup.srt", "Movie.en_original_backup")]
        public void SidecarByproduct_DoesNotCollapseOntoOriginal(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>Degenerate inputs must return something rather than throw.</summary>
        [Theory]
        [InlineData("", "")]
        [InlineData("Movie", "Movie")]
        [InlineData(".srt", "")]
        public void DegenerateInput_DoesNotThrow(string fileName, string expected)
            => Assert.Equal(expected, SubtitleMatcher.GetBaseName(fileName));

        /// <summary>
        /// KNOWN LIMITATION (review item 3.1). The tag pattern is positional -
        /// "any 2-3 word chars between the last two dots" - not a language-code
        /// lookup, so a title whose final segment is that short gets eaten, and
        /// a tag it can't describe (longer than 3 chars, or containing a
        /// non-word char like the hyphen in "pt-BR") is left on. Either way the
        /// base name misses its video and the subtitle is silently never
        /// synced. These rows document today's behaviour so a fix shows up as a
        /// deliberate change here rather than a surprise in the field.
        /// </summary>
        [Theory]
        [InlineData("Show.S01.E02.srt", "Show.S01")]            // want: Show.S01.E02
        [InlineData("Movie.4K.srt", "Movie")]                   // want: Movie.4K
        [InlineData("Movie.Part.II.srt", "Movie.Part")]         // want: Movie.Part.II
        [InlineData("Movie.en.sdh.srt", "Movie.en")]            // want: Movie
        [InlineData("Movie.pt-BR.srt", "Movie.pt-BR")]          // want: Movie
        [InlineData("Movie.forced.srt", "Movie.forced")]        // want: Movie
        public void KnownLimitation_TagDetectionIsPositional(string fileName, string current)
            => Assert.Equal(current, SubtitleMatcher.GetBaseName(fileName));
    }
}
