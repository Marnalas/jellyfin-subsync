using System.Text;
using System.Text.Json;
using Jellyfin.Subsync.Starter.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests;

/// <summary>
/// The fail-cache is what stops a nightly sweep from re-attempting a file
/// that never succeeds, so the cases that matter are the ones where it
/// could either get stuck forever (content changes but the streak doesn't
/// reset) or never kick in at all (streak resets when it shouldn't, or the
/// threshold isn't honored).
/// </summary>
public sealed class FailCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "subsync-failcache-tests", Path.GetRandomFileName());

    private readonly string _dataFolder;
    private readonly string _library;

    public FailCacheTests()
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

    private string CachePath => Path.Combine(_dataFolder, "sync-failures.json");

    private FailCache NewCache(int maxConsecutiveFailures) => new(_dataFolder, maxConsecutiveFailures, NullLogger.Instance);

    private string WriteSubtitle(string name, string content = "1\n00:00:01,000 --> 00:00:02,000\nhello\n")
    {
        var path = Path.Combine(_library, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed record RawEntry(string ContentHash, int ConsecutiveFailures);

    private Dictionary<string, RawEntry> ReadRawCache() =>
        JsonSerializer.Deserialize<Dictionary<string, RawEntry>>(File.ReadAllText(CachePath))!;

    [Fact]
    public void BelowThreshold_IsNotSkipped()
    {
        var subtitle = WriteSubtitle("a.srt");

        using var cache = NewCache(3);
        cache.AddToCache(subtitle);
        cache.AddToCache(subtitle);

        Assert.False(cache.IsCached(subtitle));
    }

    [Fact]
    public void AtThreshold_IsSkipped()
    {
        var subtitle = WriteSubtitle("a.srt");

        using var cache = NewCache(3);
        for (var i = 0; i < 3; i++)
            cache.AddToCache(subtitle);

        Assert.True(cache.IsCached(subtitle));
    }

    [Fact]
    public void ZeroThreshold_DisablesTheCheck()
    {
        var subtitle = WriteSubtitle("a.srt");

        using var cache = NewCache(0);
        for (var i = 0; i < 10; i++)
            cache.AddToCache(subtitle);

        Assert.False(cache.IsCached(subtitle));
    }

    /// <summary>
    /// A file the user has since replaced or fixed must get a fresh
    /// attempt, not stay skipped because of bytes that no longer exist.
    /// </summary>
    [Fact]
    public void ChangedContent_ResetsTheStreak()
    {
        var subtitle = WriteSubtitle("a.srt");

        using var cache = NewCache(3);
        for (var i = 0; i < 3; i++)
            cache.AddToCache(subtitle);
        Assert.True(cache.IsCached(subtitle));

        File.WriteAllText(subtitle, "different content entirely");

        Assert.False(cache.IsCached(subtitle));

        cache.AddToCache(subtitle);
        cache.Flush();
        Assert.Equal(1, ReadRawCache()[subtitle].ConsecutiveFailures);
    }

    [Fact]
    public void ClearFailures_RemovesTheStreak()
    {
        var subtitle = WriteSubtitle("a.srt");

        using var cache = NewCache(3);
        for (var i = 0; i < 3; i++)
            cache.AddToCache(subtitle);

        cache.RemoveForPath(subtitle);

        Assert.False(cache.IsCached(subtitle));
    }

    [Fact]
    public void Entries_RoundTripAcrossInstances()
    {
        var subtitle = WriteSubtitle("a.srt");

        using (var first = NewCache(3))
        {
            for (var i = 0; i < 3; i++)
                first.AddToCache(subtitle);
            first.Flush();
        }

        using var second = NewCache(3);
        Assert.True(second.IsCached(subtitle));
    }

    [Fact]
    public void Saves_AreBatchedRatherThanOnePerFailure()
    {
        using var cache = NewCache(3);

        for (var i = 0; i < 5; i++)
            cache.AddToCache(WriteSubtitle($"batch{i}.srt", $"content {i}"));

        Assert.False(File.Exists(CachePath), "a handful of failures should not have rewritten the cache file yet");

        cache.Flush();
        Assert.Equal(5, ReadRawCache().Count);
    }

    [Fact]
    public void Dispose_FlushesPendingFailures()
    {
        var subtitle = WriteSubtitle("a.srt");

        using (var cache = NewCache(3))
            cache.AddToCache(subtitle);

        Assert.True(File.Exists(CachePath));
        Assert.Single(ReadRawCache());
    }

    [Fact]
    public void RemoveMissingFiles_DropsDeletedFiles()
    {
        var kept = new[]
            { WriteSubtitle("keep1.srt", "1"), WriteSubtitle("keep2.srt", "2"), WriteSubtitle("keep3.srt", "3") };
        var deleted = WriteSubtitle("gone.srt", "4");

        using var cache = NewCache(3);
        foreach (var path in kept.Append(deleted))
            cache.AddToCache(path);

        File.Delete(deleted);

        Assert.Equal(1, cache.RemoveMissingFiles());
        cache.Flush();

        var remaining = ReadRawCache();
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(deleted, remaining.Keys);
    }

    /// <summary>
    /// Same mount-outage guard as SkipCache: an unmounted volume must not
    /// be read as a wholesale deletion of everything tracked under it.
    /// </summary>
    [Fact]
    public void RemoveMissingFiles_KeepsEntriesWhoseWholeDirectoryIsGone()
    {
        var unmounted = Path.Combine(_root, "not-mounted");
        Directory.CreateDirectory(unmounted);
        var vanished = Path.Combine(unmounted, "a.srt");
        File.WriteAllText(vanished, "content");

        var kept = new[] { WriteSubtitle("keep1.srt", "1"), WriteSubtitle("keep2.srt", "2") };

        using var cache = NewCache(3);
        cache.AddToCache(vanished);
        foreach (var path in kept)
            cache.AddToCache(path);

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

        using var cache = NewCache(3);
        foreach (var path in paths)
            cache.AddToCache(path);
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

        using var cache = NewCache(1);
        Assert.False(cache.IsCached(subtitle));
    }

    [Fact]
    public void Clear_RemovesEverythingAndPersistsImmediately()
    {
        var paths = new[] { WriteSubtitle("a.srt", "1"), WriteSubtitle("b.srt", "2") };

        using var cache = NewCache(1);
        foreach (var path in paths)
            cache.AddToCache(path);

        Assert.Equal(2, cache.Clear());
        Assert.Empty(ReadRawCache());
        Assert.False(cache.IsCached(paths[0]));
    }

    [Fact]
    public void Clear_OnAnEmptyCache_IsANoOp()
    {
        using var cache = NewCache(3);
        Assert.Equal(0, cache.Clear());
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void RemoveForPaths_RemovesOnlyTheGivenPathsAndPersistsImmediately()
    {
        var kept = WriteSubtitle("keep.srt", "1");
        var removed = WriteSubtitle("remove.srt", "2");

        using var cache = NewCache(1);
        cache.AddToCache(kept);
        cache.AddToCache(removed);

        Assert.Equal(1, cache.RemoveForPaths([removed, "/never/tracked.srt"]));

        var remaining = ReadRawCache();
        Assert.Single(remaining);
        Assert.Contains(kept, remaining.Keys);
        Assert.False(cache.IsCached(removed));
    }

    [Fact]
    public void RemoveForPaths_WithNoMatches_IsANoOp()
    {
        var kept = WriteSubtitle("keep.srt", "1");

        using var cache = NewCache(3);
        cache.AddToCache(kept);
        cache.Flush();

        Assert.Equal(0, cache.RemoveForPaths(["/never/tracked.srt"]));
        Assert.Single(ReadRawCache());
    }
}