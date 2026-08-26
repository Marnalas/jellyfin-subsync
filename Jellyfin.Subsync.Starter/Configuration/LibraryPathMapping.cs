namespace Jellyfin.Subsync.Starter.Configuration;

/// <summary>One entry of <see cref="PluginConfiguration.LibraryPathMappings"/>, admin-facing per-library path mapping.</summary>
public class LibraryPathMapping
{
    /// <summary>The library's VirtualFolderInfo.ItemId, so a rename doesn't orphan the mapping.</summary>
    public Guid LibraryId { get; set; } = Guid.Empty;

    /// <summary>Library name at the time it was last saved here, shown if the library can no longer be found on the server.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Whether this library's mappings feed into the effective WatchedPathsMaps. Every library starts disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>One entry per library location, JellyfinPath = that location, SidecarPath = the admin-entered equivalent.</summary>
    public List<PathMapEntry> PathMappings { get; set; } = [];
}