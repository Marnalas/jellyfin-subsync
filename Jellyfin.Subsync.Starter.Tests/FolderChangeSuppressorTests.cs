using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests;

/// <summary>
/// Jellyfin's own ignore-set is a flat dictionary, not reference-counted:
/// two concurrent writers to the same folder would otherwise let whichever
/// finishes first re-arm the watcher on its still-writing sibling. These
/// tests pin the ref-counting that keeps a folder suppressed until every
/// concurrent caller is done with it.
/// </summary>
public sealed class FolderChangeSuppressorTests
{
    private sealed class FakeLibraryMonitor : ILibraryMonitor
    {
        public List<string> BeginningCalls { get; } = [];
        public List<(string Path, bool RefreshPath)> CompleteCalls { get; } = [];

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void ReportFileSystemChangeBeginning(string path) => BeginningCalls.Add(path);

        public void ReportFileSystemChangeComplete(string path, bool refreshPath) =>
            CompleteCalls.Add((path, refreshPath));

        public void ReportFileSystemChanged(string path)
        {
        }
    }

    [Fact]
    public void SingleSuppression_ReportsBeginningThenCompleteOnDispose()
    {
        var monitor = new FakeLibraryMonitor();
        var suppressor = new FolderChangeSuppressor(monitor);

        var scope = suppressor.Suppress("/library/Show/Season 1");
        Assert.Equal(["/library/Show/Season 1"], monitor.BeginningCalls);
        Assert.Empty(monitor.CompleteCalls);

        scope.Dispose();

        Assert.Single(monitor.CompleteCalls);
        Assert.Equal("/library/Show/Season 1", monitor.CompleteCalls[0].Path);
        Assert.False(monitor.CompleteCalls[0].RefreshPath);
    }

    [Fact]
    public void TwoConcurrentSuppressionsOnTheSameFolder_OnlyReportCompleteAfterBothAreDisposed()
    {
        var monitor = new FakeLibraryMonitor();
        var suppressor = new FolderChangeSuppressor(monitor);
        var folder = "/library/Show/Season 1";

        var first = suppressor.Suppress(folder);
        var second = suppressor.Suppress(folder);

        // Only the first caller should have armed the ignore - a second
        // ReportFileSystemChangeBeginning for the same path would just be
        // redundant noise on Jellyfin's side.
        Assert.Single(monitor.BeginningCalls);

        first.Dispose();
        Assert.Empty(monitor.CompleteCalls);

        second.Dispose();
        Assert.Single(monitor.CompleteCalls);
    }

    [Fact]
    public void DifferentFolders_AreTrackedIndependently()
    {
        var monitor = new FakeLibraryMonitor();
        var suppressor = new FolderChangeSuppressor(monitor);

        var a = suppressor.Suppress("/library/Show/Season 1");
        var b = suppressor.Suppress("/library/Show/Season 2");

        Assert.Equal(2, monitor.BeginningCalls.Count);

        a.Dispose();
        Assert.Single(monitor.CompleteCalls);

        b.Dispose();
        Assert.Equal(2, monitor.CompleteCalls.Count);
    }

    [Fact]
    public void DisposingTheSameScopeTwice_OnlyReportsCompleteOnce()
    {
        var monitor = new FakeLibraryMonitor();
        var suppressor = new FolderChangeSuppressor(monitor);

        var scope = suppressor.Suppress("/library/Movie");
        scope.Dispose();
        scope.Dispose();

        Assert.Single(monitor.CompleteCalls);
    }
}

