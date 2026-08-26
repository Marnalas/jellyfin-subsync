using Jellyfin.Subsync.Starter.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// Probes the sidecar's /health a few times before committing to work that
/// depends on it being up - shared so the sweep and the single-item sync
/// endpoint fail the same way instead of drifting apart.
/// </summary>
internal static class SidecarHealthChecker
{
    /// <summary>
    /// A "Run Now" fired seconds after `docker compose up` shouldn't abort
    /// on a sidecar that's still importing ffsubsync, so the check gets a
    /// few tries before giving up on it.
    /// </summary>
    internal const int Attempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    internal static async Task<bool> IsReachableAsync(
        ISubsyncClient client, PluginConfiguration config, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (await client.IsHealthyAsync(config, cancellationToken).ConfigureAwait(false))
                return true;

            if (attempt >= Attempts) continue;
            logger.LogWarning(
                "Subsync: the sidecar didn't answer /health (attempt {Attempt} of {Attempts}), retrying",
                attempt, Attempts);
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
