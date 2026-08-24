namespace Jellyfin.Subsync.Starter.Configuration;

/// <summary>
/// The one place that still reaches for Plugin.Instance. Everything
/// downstream takes the PluginConfiguration it hands back as a plain
/// parameter, which is what lets the client, the orchestrator and the sweep
/// be exercised without a running Jellyfin - and what lets a sweep read the
/// configuration once at the start instead of re-reading it per file, so a
/// save halfway through a multi-hour run can't change the rules mid-flight.
/// </summary>
public interface IPluginConfigurationProvider
{
    PluginConfiguration GetSnapshot();
}

/// <inheritdoc />
public sealed class PluginConfigurationProvider : IPluginConfigurationProvider
{
    /// <remarks>
    /// BasePlugin.UpdateConfiguration assigns a new configuration instance
    /// rather than mutating the existing one, so holding the returned
    /// reference for the length of a sweep is already a snapshot - no
    /// defensive copy needed.
    /// </remarks>
    public PluginConfiguration GetSnapshot() =>
        Plugin.Instance?.Configuration
        ?? throw new InvalidOperationException("The Subsync plugin is not loaded");
}

