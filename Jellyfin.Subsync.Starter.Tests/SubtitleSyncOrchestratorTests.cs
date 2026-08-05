using Jellyfin.Subsync.Starter.Application;
using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using Jellyfin.Subsync.Starter.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests
{
    /// <summary>
    /// The orchestrator decides what gets submitted and what gets recorded as
    /// synced. Recording something that wasn't actually written is what makes a
    /// subtitle silently stay out of sync forever, so the outcome-to-MarkSynced
    /// mapping is the part worth pinning.
    /// </summary>
    public sealed class SubtitleSyncOrchestratorTests : IDisposable
    {
        private readonly string _library = Path.Combine(Path.GetTempPath(), "subsync-orchestrator-tests", Path.GetRandomFileName());

        public SubtitleSyncOrchestratorTests() => Directory.CreateDirectory(_library);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_library, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        private sealed class FakeSubsyncClient(SyncOutcome outcome) : ISubsyncClient
        {
            public List<(string Folder, string Reference, string Subtitle)> Calls { get; } = [];

            public Task<bool> IsHealthyAsync(PluginConfiguration config, CancellationToken cancellationToken) =>
                Task.FromResult(true);

            public Task<SyncOutcome> SyncAndWaitAsync(
                PluginConfiguration config, string folder, string referenceFilename, string subtitleFilename, CancellationToken cancellationToken)
            {
                Calls.Add((folder, referenceFilename, subtitleFilename));
                return Task.FromResult(outcome);
            }
        }

        private sealed class FakeFolderChangeSuppressor : IFolderChangeSuppressor
        {
            public List<string> ActiveFolders { get; } = [];

            public IDisposable Suppress(string folder)
            {
                ActiveFolders.Add(folder);
                return new Scope(this, folder);
            }

            private sealed class Scope(FakeFolderChangeSuppressor owner, string folder) : IDisposable
            {
                public void Dispose() => owner.ActiveFolders.Remove(folder);
            }
        }

        private sealed class FakeSkipCache : ISkipCache
        {
            public HashSet<string> Synced { get; } = [];
            public List<string> Marked { get; } = [];

            public bool IsAlreadySynced(string subtitlePath) => Synced.Contains(subtitlePath);

            public void MarkSynced(string subtitlePath)
            {
                Marked.Add(subtitlePath);
                Synced.Add(subtitlePath);
            }

            public void Flush()
            {
            }

            public int RemoveMissingFiles() => 0;

            public void Dispose()
            {
            }
        }

        private PluginConfiguration Config(bool mapped = true) => new()
        {
            SidecarUrl = "http://sidecar:8000",
            WatchedPathsMaps = mapped
                ? [new PathMapEntry { JellyfinPath = _library, SidecarPath = "/media/sidecar" }]
                : []
        };

        private string Write(string name)
        {
            var path = Path.Combine(_library, name);
            File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nhello\n");
            return path;
        }

        [Theory]
        [InlineData(SyncOutcome.Synced, true)]
        [InlineData(SyncOutcome.Failed, false)]
        [InlineData(SyncOutcome.QueueTimedOut, false)]
        [InlineData(SyncOutcome.RunTimedOut, false)]
        [InlineData(SyncOutcome.JobUnknown, false)]
        [InlineData(SyncOutcome.SidecarUnreachable, false)]
        public async Task OnlyAConfirmedSync_IsRecorded(SyncOutcome outcome, bool expectMarked)
        {
            var video = Write("Movie.mkv");
            var subtitle = Write("Movie.en.srt");
            var client = new FakeSubsyncClient(outcome);
            var skipCache = new FakeSkipCache();
            var orchestrator = new SubtitleSyncOrchestrator(client, skipCache, NullLogger.Instance, new FakeFolderChangeSuppressor());

            var result = await orchestrator.ProcessAsync(
                Config(), new SubtitleSyncGroup(video, [subtitle]), subtitle, CancellationToken.None);

            Assert.Equal(outcome, result);
            Assert.Single(client.Calls);
            Assert.Equal(expectMarked ? [subtitle] : (List<string>)[], skipCache.Marked);
        }

        [Fact]
        public async Task AlreadySyncedSubtitle_IsNotSubmitted()
        {
            var video = Write("Movie.mkv");
            var subtitle = Write("Movie.en.srt");
            var client = new FakeSubsyncClient(SyncOutcome.Synced);
            var skipCache = new FakeSkipCache();
            skipCache.Synced.Add(subtitle);
            var orchestrator = new SubtitleSyncOrchestrator(client, skipCache, NullLogger.Instance, new FakeFolderChangeSuppressor());

            var result = await orchestrator.ProcessAsync(
                Config(), new SubtitleSyncGroup(video, [subtitle]), subtitle, CancellationToken.None);

            Assert.Null(result);
            Assert.Empty(client.Calls);
        }

        [Fact]
        public async Task SubtitleDeletedSinceTheLibraryScan_IsSkippedWithoutThrowing()
        {
            var video = Write("Movie.mkv");
            var subtitle = Path.Combine(_library, "never-existed.srt");
            var client = new FakeSubsyncClient(SyncOutcome.Synced);
            var orchestrator = new SubtitleSyncOrchestrator(client, new FakeSkipCache(), NullLogger.Instance, new FakeFolderChangeSuppressor());

            var result = await orchestrator.ProcessAsync(
                Config(), new SubtitleSyncGroup(video, [subtitle]), subtitle, CancellationToken.None);

            Assert.Null(result);
            Assert.Empty(client.Calls);
        }

        [Fact]
        public async Task UnmappedPath_IsSkippedWithoutCallingTheSidecar()
        {
            var video = Write("Movie.mkv");
            var subtitle = Write("Movie.en.srt");
            var client = new FakeSubsyncClient(SyncOutcome.Synced);
            var orchestrator = new SubtitleSyncOrchestrator(client, new FakeSkipCache(), NullLogger.Instance, new FakeFolderChangeSuppressor());

            var result = await orchestrator.ProcessAsync(
                Config(mapped: false), new SubtitleSyncGroup(video, [subtitle]), subtitle, CancellationToken.None);

            Assert.Null(result);
            Assert.Empty(client.Calls);
        }

        [Fact]
        public async Task PathsHandedToTheSidecar_AreTranslatedToItsSideOfTheMount()
        {
            var video = Write("Movie.mkv");
            var subtitle = Write("Movie.en.srt");
            var client = new FakeSubsyncClient(SyncOutcome.Synced);
            var orchestrator = new SubtitleSyncOrchestrator(client, new FakeSkipCache(), NullLogger.Instance, new FakeFolderChangeSuppressor());

            await orchestrator.ProcessAsync(
                Config(), new SubtitleSyncGroup(video, [subtitle]), subtitle, CancellationToken.None);

            var (folder, reference, sub) = Assert.Single(client.Calls);
            Assert.Equal("/media/sidecar", folder);
            Assert.Equal("Movie.mkv", reference);
            Assert.Equal("Movie.en.srt", sub);
        }

        /// <summary>
        /// Once one subtitle of a video has been synced, the rest align against
        /// that instead of the video file - subtitle-to-subtitle is far cheaper
        /// than decoding the audio track again.
        /// </summary>
        [Fact]
        public async Task AlreadySyncedSibling_IsPreferredOverTheVideoAsReference()
        {
            var video = Write("Movie.mkv");
            var synced = Write("Movie.en.srt");
            var pending = Write("Movie.fr.srt");
            var client = new FakeSubsyncClient(SyncOutcome.Synced);
            var skipCache = new FakeSkipCache();
            skipCache.Synced.Add(synced);
            var orchestrator = new SubtitleSyncOrchestrator(client, skipCache, NullLogger.Instance, new FakeFolderChangeSuppressor());

            await orchestrator.ProcessAsync(
                Config(), new SubtitleSyncGroup(video, [synced, pending]), pending, CancellationToken.None);

            var (_, reference, sub) = Assert.Single(client.Calls);
            Assert.Equal("Movie.en.srt", reference);
            Assert.Equal("Movie.fr.srt", sub);
        }

        /// <summary>
        /// The subtitle's containing folder - not the subtitle path itself -
        /// must be suppressed for the whole sidecar round-trip: the sidecar's
        /// temp and backup files sit next to the subtitle under different
        /// names, and all of them need to stay invisible to Jellyfin's watcher.
        /// </summary>
        [Fact]
        public async Task Sync_SuppressesTheSubtitlesContainingFolderForTheDurationOfTheCall()
        {
            var video = Write("Movie.mkv");
            var subtitle = Write("Movie.en.srt");
            var suppressor = new FakeFolderChangeSuppressor();
            var client = new SuppressionObservingSubsyncClient(suppressor);
            var orchestrator = new SubtitleSyncOrchestrator(client, new FakeSkipCache(), NullLogger.Instance, suppressor);

            await orchestrator.ProcessAsync(
                Config(), new SubtitleSyncGroup(video, [subtitle]), subtitle, CancellationToken.None);

            Assert.Equal([_library], client.ActiveFoldersDuringCall);
            Assert.Empty(suppressor.ActiveFolders);
        }

        /// <summary>
        /// A crash mid-sync must not leave the folder permanently suppressed:
        /// the next sweep still needs Jellyfin's watcher armed for it.
        /// </summary>
        [Fact]
        public async Task Sync_ReleasesTheSuppressionEvenWhenTheSidecarCallThrows()
        {
            var video = Write("Movie.mkv");
            var subtitle = Write("Movie.en.srt");
            var suppressor = new FakeFolderChangeSuppressor();
            var orchestrator = new SubtitleSyncOrchestrator(
                new ThrowingSubsyncClient(), new FakeSkipCache(), NullLogger.Instance, suppressor);

            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ProcessAsync(
                Config(), new SubtitleSyncGroup(video, [subtitle]), subtitle, CancellationToken.None));

            Assert.Empty(suppressor.ActiveFolders);
        }

        private sealed class ThrowingSubsyncClient : ISubsyncClient
        {
            public Task<bool> IsHealthyAsync(PluginConfiguration config, CancellationToken cancellationToken) =>
                Task.FromResult(true);

            public Task<SyncOutcome> SyncAndWaitAsync(
                PluginConfiguration config, string folder, string referenceFilename, string subtitleFilename, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("sidecar exploded");
        }

        private sealed class SuppressionObservingSubsyncClient(FakeFolderChangeSuppressor suppressor) : ISubsyncClient
        {
            public List<string> ActiveFoldersDuringCall { get; } = [];

            public Task<bool> IsHealthyAsync(PluginConfiguration config, CancellationToken cancellationToken) =>
                Task.FromResult(true);

            public Task<SyncOutcome> SyncAndWaitAsync(
                PluginConfiguration config, string folder, string referenceFilename, string subtitleFilename, CancellationToken cancellationToken)
            {
                ActiveFoldersDuringCall.AddRange(suppressor.ActiveFolders);
                return Task.FromResult(SyncOutcome.Synced);
            }
        }
    }
}
