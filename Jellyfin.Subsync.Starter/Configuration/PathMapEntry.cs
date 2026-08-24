namespace Jellyfin.Subsync.Starter.Configuration;

/// <summary>One entry of <see cref="PluginConfiguration.WatchedPathsMaps"/>.</summary>
public class PathMapEntry
{
    /// <summary>Path as seen INSIDE the Jellyfin container.</summary>
    public string JellyfinPath { get; init; } = string.Empty;

    /// <summary>Equivalent path as seen by the subsync-sidecar container.</summary>
    public string SidecarPath { get; init; } = string.Empty;
}

