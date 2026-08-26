using Jellyfin.Subsync.Starter.Configuration;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests;

/// <summary>
/// <see cref="PluginConfigurationHelper.DeriveWatchedPathsMaps"/> flattens
/// the admin-facing per-library mappings into the flat list
/// <see cref="Infrastructure.SubtitleMatcher.ToSidecarAbsolute"/> actually
/// consumes. A wrong answer here either sweeps a library the admin
/// disabled, or silently drops one they enabled.
/// </summary>
public class PluginConfigurationHelperTests
{
    private static LibraryPathMapping Library(bool enabled, params (string Jellyfin, string Sidecar)[] maps)
        => new()
        {
            LibraryId = Guid.NewGuid(),
            LibraryName = "Library",
            Enabled = enabled,
            PathMappings = [.. maps.Select(map => new PathMapEntry
            {
                JellyfinPath = map.Jellyfin,
                SidecarPath = map.Sidecar
            })]
        };

    [Fact]
    public void EnabledLibrary_ContributesItsMappings()
    {
        var config = new PluginConfiguration
        {
            LibraryPathMappings = [Library(true, ("/media/Movies", "/mnt/Movies"))]
        };

        config.DeriveWatchedPathsMaps();

        var entry = Assert.Single(config.WatchedPathsMaps);
        Assert.Equal("/media/Movies", entry.JellyfinPath);
        Assert.Equal("/mnt/Movies", entry.SidecarPath);
    }

    [Fact]
    public void DisabledLibrary_ContributesNothingEvenIfPopulated()
    {
        var config = new PluginConfiguration
        {
            LibraryPathMappings = [Library(false, ("/media/Movies", "/mnt/Movies"))]
        };

        config.DeriveWatchedPathsMaps();

        Assert.Empty(config.WatchedPathsMaps);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnabledLibrary_DropsEntriesWithBlankSidecarPath(string blankSidecarPath)
    {
        var config = new PluginConfiguration
        {
            LibraryPathMappings = [Library(true, ("/media/Movies", blankSidecarPath))]
        };

        config.DeriveWatchedPathsMaps();

        Assert.Empty(config.WatchedPathsMaps);
    }

    [Fact]
    public void EnabledLibraryWithMultipleLocations_EachProducesItsOwnEntry()
    {
        var config = new PluginConfiguration
        {
            LibraryPathMappings =
            [
                Library(true, ("/media/Movies/A", "/mnt/A"), ("/media/Movies/B", "/mnt/B"))
            ]
        };

        config.DeriveWatchedPathsMaps();

        Assert.Equal(2, config.WatchedPathsMaps.Count);
        Assert.Contains(config.WatchedPathsMaps, e => e.JellyfinPath == "/media/Movies/A" && e.SidecarPath == "/mnt/A");
        Assert.Contains(config.WatchedPathsMaps, e => e.JellyfinPath == "/media/Movies/B" && e.SidecarPath == "/mnt/B");
    }

    [Fact]
    public void MixOfEnabledAndDisabledLibraries_OnlyEnabledOnesContribute()
    {
        var config = new PluginConfiguration
        {
            LibraryPathMappings =
            [
                Library(true, ("/media/Movies", "/mnt/Movies")),
                Library(false, ("/media/Shows", "/mnt/Shows"))
            ]
        };

        config.DeriveWatchedPathsMaps();

        var entry = Assert.Single(config.WatchedPathsMaps);
        Assert.Equal("/media/Movies", entry.JellyfinPath);
    }

    [Fact]
    public void NoLibraryMappingsConfigured_ProducesEmptyWatchedPathsMaps()
    {
        var config = new PluginConfiguration();

        config.DeriveWatchedPathsMaps();

        Assert.Empty(config.WatchedPathsMaps);
    }
}