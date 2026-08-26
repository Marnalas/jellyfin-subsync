using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Subsync.Starter.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests
{
    /// <summary>
    /// The skip-cache is what keeps a nightly sweep from re-syncing the whole
    /// library, so the cases that matter are the ones where it could silently
    /// forget something: the MD5-to-SHA-256 migration, batched saves, and
    /// pruning entries for files that only look missing.
    /// </summary>
    public sealed class SkipCacheTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "subsync-skipcache-tests", Path.GetRandomFileName());

        private readonly string _dataFolder;
        private readonly string _library;

        public SkipCacheTests()
        {
            _dataFolder = Path.Combine(_root, "data");
            _library = Path.Combine(_root, "library");
            Directory.CreateDirectory(_dataFolder);
            Directory.CreateDirectory(_library);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        private string CachePath => Path.Combine(_dataFolder, "skip-cache.json");

        private SkipCache NewCache() => new(_dataFolder, NullLogger.Instance);

        private string WriteSubtitle(string name, string content = "1\n00:00:01,000 --> 00:00:02,000\nhello\n")
        {
            var path = Path.Combine(_library, name);
            File.WriteAllText(path, content);
            return path;
        }

        private void WriteRawCache(Dictionary<string, string> entries) =>
            File.WriteAllText(CachePath, JsonSerializer.Serialize(entries));

        private Dictionary<string, string> ReadRawCache() =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath))!;

        private static string LegacyMd5Of(string path) =>
            Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path)));

        [Fact]
        public void MarkedFile_IsSkippedOnceFlushed()
        {
            var subtitle = WriteSubtitle("a.srt");

            using var cache = NewCache();
            Assert.False(cache.IsAlreadySynced(subtitle));

            cache.MarkSynced(subtitle);
            Assert.True(cache.IsAlreadySynced(subtitle));
        }

        [Fact]
        public void ChangedContent_IsNotSkipped()
        {
            var subtitle = WriteSubtitle("a.srt");

            using var cache = NewCache();
            cache.MarkSynced(subtitle);

            File.WriteAllText(subtitle, "different content entirely");
            Assert.False(cache.IsAlreadySynced(subtitle));
        }

        [Fact]
        public void Entries_RoundTripAcrossInstances()
        {
            var subtitle = WriteSubtitle("a.srt");

            using (var first = NewCache())
            {
                first.MarkSynced(subtitle);
                first.Flush();
            }

            using var second = NewCache();
            Assert.True(second.IsAlreadySynced(subtitle));
        }

        [Fact]
        public void NewEntries_AreWrittenWithTheAlgorithmPrefix()
        {
            var subtitle = WriteSubtitle("a.srt");

            using var cache = NewCache();
            cache.MarkSynced(subtitle);
            cache.Flush();

            Assert.StartsWith("sha256:", ReadRawCache()[subtitle], StringComparison.Ordinal);
        }

        /// <summary>
        /// The migration case that matters: an upgrade must not re-sync a
        /// library that was already fully synced under the old MD5 format.
        /// </summary>
        [Fact]
        public void LegacyMd5Entry_ThatStillMatches_IsHonouredAndUpgradedInPlace()
        {
            var subtitle = WriteSubtitle("a.srt");
            WriteRawCache(new Dictionary<string, string> { [subtitle] = LegacyMd5Of(subtitle) });

            using var cache = NewCache();
            Assert.True(cache.IsAlreadySynced(subtitle));

            cache.Flush();
            Assert.StartsWith("sha256:", ReadRawCache()[subtitle], StringComparison.Ordinal);
        }

        [Fact]
        public void LegacyMd5Entry_ForChangedContent_IsNotSkipped()
        {
            var subtitle = WriteSubtitle("a.srt");
            WriteRawCache(new Dictionary<string, string> { [subtitle] = LegacyMd5Of(subtitle) });
            File.WriteAllText(subtitle, "the file changed after it was recorded");

            using var cache = NewCache();
            Assert.False(cache.IsAlreadySynced(subtitle));
        }

        [Fact]
        public void Saves_AreBatchedRatherThanOnePerMark()
        {
            using var cache = NewCache();

            for (var i = 0; i < 5; i++)
                cache.MarkSynced(WriteSubtitle($"batch{i}.srt", $"content {i}"));

            Assert.False(File.Exists(CachePath), "a handful of marks should not have rewritten the cache file yet");

            cache.Flush();
            Assert.Equal(5, ReadRawCache().Count);
        }

        [Fact]
        public void Dispose_FlushesPendingMarks()
        {
            var subtitle = WriteSubtitle("a.srt");

            using (var cache = NewCache())
                cache.MarkSynced(subtitle);

            Assert.True(File.Exists(CachePath));
            Assert.Single(ReadRawCache());
        }

        [Fact]
        public void RemoveMissingFiles_DropsDeletedFiles()
        {
            var kept = new[]
                { WriteSubtitle("keep1.srt", "1"), WriteSubtitle("keep2.srt", "2"), WriteSubtitle("keep3.srt", "3") };
            var deleted = WriteSubtitle("gone.srt", "4");

            using var cache = NewCache();
            foreach (var path in kept.Append(deleted))
                cache.MarkSynced(path);

            File.Delete(deleted);

            Assert.Equal(1, cache.RemoveMissingFiles());
            cache.Flush();

            var remaining = ReadRawCache();
            Assert.Equal(3, remaining.Count);
            Assert.DoesNotContain(deleted, remaining.Keys);
        }

        /// <summary>
        /// An unmounted volume looks exactly like a deleted library. Pruning it
        /// would throw away the whole cache and re-sync everything on the next
        /// sweep, so a missing directory means "leave it alone".
        /// </summary>
        [Fact]
        public void RemoveMissingFiles_KeepsEntriesWhoseWholeDirectoryIsGone()
        {
            var unmounted = Path.Combine(_root, "not-mounted");
            Directory.CreateDirectory(unmounted);
            var vanished = Path.Combine(unmounted, "a.srt");
            File.WriteAllText(vanished, "content");

            var kept = new[] { WriteSubtitle("keep1.srt", "1"), WriteSubtitle("keep2.srt", "2") };

            using var cache = NewCache();
            cache.MarkSynced(vanished);
            foreach (var path in kept)
                cache.MarkSynced(path);

            Directory.Delete(unmounted, recursive: true);

            Assert.Equal(0, cache.RemoveMissingFiles());
            cache.Flush();
            Assert.Contains(vanished, ReadRawCache().Keys);
        }

        [Fact]
        public void RemoveMissingFiles_RefusesWhenMostOfTheCacheLooksMissing()
        {
            var paths = new[]
            {
                WriteSubtitle("a.srt", "1"), WriteSubtitle("b.srt", "2"),
                WriteSubtitle("c.srt", "3"), WriteSubtitle("d.srt", "4")
            };

            using var cache = NewCache();
            foreach (var path in paths)
                cache.MarkSynced(path);
            cache.Flush();

            foreach (var path in paths.Take(3))
                File.Delete(path);

            Assert.Equal(0, cache.RemoveMissingFiles());
            Assert.Equal(4, ReadRawCache().Count);
        }

        [Fact]
        public void CorruptCacheFile_StartsEmptyInsteadOfThrowing()
        {
            File.WriteAllText(CachePath, "{ this is not json", Encoding.UTF8);
            var subtitle = WriteSubtitle("a.srt");

            using var cache = NewCache();
            Assert.False(cache.IsAlreadySynced(subtitle));
        }

        [Fact]
        public void Clear_RemovesEverythingAndPersistsImmediately()
        {
            var paths = new[] { WriteSubtitle("a.srt", "1"), WriteSubtitle("b.srt", "2") };

            using var cache = NewCache();
            foreach (var path in paths)
                cache.MarkSynced(path);

            Assert.Equal(2, cache.Clear());
            Assert.Empty(ReadRawCache());
            Assert.False(cache.IsAlreadySynced(paths[0]));
        }

        [Fact]
        public void Clear_OnAnEmptyCache_IsANoOp()
        {
            using var cache = NewCache();
            Assert.Equal(0, cache.Clear());
            Assert.False(File.Exists(CachePath));
        }

        [Fact]
        public void RemoveForPaths_RemovesOnlyTheGivenPathsAndPersistsImmediately()
        {
            var kept = WriteSubtitle("keep.srt", "1");
            var removed = WriteSubtitle("remove.srt", "2");

            using var cache = NewCache();
            cache.MarkSynced(kept);
            cache.MarkSynced(removed);

            Assert.Equal(1, cache.RemoveForPaths([removed, "/never/tracked.srt"]));

            var remaining = ReadRawCache();
            Assert.Single(remaining);
            Assert.Contains(kept, remaining.Keys);
            Assert.False(cache.IsAlreadySynced(removed));
        }

        [Fact]
        public void RemoveForPaths_WithNoMatches_IsANoOp()
        {
            var kept = WriteSubtitle("keep.srt", "1");

            using var cache = NewCache();
            cache.MarkSynced(kept);
            cache.Flush();

            Assert.Equal(0, cache.RemoveForPaths(["/never/tracked.srt"]));
            Assert.Single(ReadRawCache());
        }
    }
}
