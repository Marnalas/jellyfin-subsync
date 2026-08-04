using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Subsync.Starter.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    /// <summary>
    /// Talks to the subsync-sidecar: submit a job, poll it to a terminal state,
    /// and tell the sidecar when we've stopped waiting for one.
    /// </summary>
    public sealed class SubsyncClient(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : ISubsyncClient
    {
        /// <summary>
        /// How far past the sidecar's own timeout this side waits before
        /// declaring a job lost. Deliberately not a config field: the invariant
        /// it protects - the sidecar always times out first, so a job we
        /// abandon is one it has given up on too - isn't something a user
        /// should be able to invert by typing a smaller number.
        /// </summary>
        private const int ClientGraceSeconds = 60;

        /// <summary>
        /// Polling failures in a row before this side stops waiting. A
        /// restarted sidecar answers 404, which is terminal on its own; this
        /// bounds the other case, where it stops answering at all, to roughly
        /// this many request timeouts rather than the whole job budget.
        /// </summary>
        private const int MaxConsecutivePollFailures = 5;

        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly ILogger _logger = logger;
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        public async Task<bool> IsHealthyAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            var baseUrl = config.SidecarUrl.TrimEnd('/');

            try
            {
                using var http = CreateClient(config);
                using var response = await http.GetAsync($"{baseUrl}/health", cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return true;

                _logger.LogError(
                    "Subsync: the sidecar at {Url} answered /health with {Status}",
                    baseUrl, (int)response.StatusCode);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subsync: the sidecar at {Url} did not answer /health", baseUrl);
                return false;
            }
        }

        public async Task<SyncOutcome> SyncAndWaitAsync(
            PluginConfiguration config,
            string folder,
            string referenceFilename,
            string subtitleFilename,
            CancellationToken cancellationToken)
        {
            var baseUrl = config.SidecarUrl.TrimEnd('/');
            var requestedTimeout = Math.Max(1, config.JobTimeoutSeconds);
            var pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(config.PollIntervalMilliseconds, 250, 60_000));

            SyncJobResponse? created;
            try
            {
                using var http = CreateClient(config);
                using var response = await http.PostAsJsonAsync(
                    $"{baseUrl}/sync",
                    new SyncRequest(folder, referenceFilename, subtitleFilename, requestedTimeout),
                    cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    // The sidecar understood the request and refused it.
                    // Re-sending identical input won't change the answer, so
                    // this is the file's problem and not the sidecar's - it
                    // must not be reported as "sidecar unreachable".
                    _logger.LogError(
                        "Subsync: the sidecar rejected the job for {Subtitle} with {Status}",
                        subtitleFilename, (int)response.StatusCode);
                    return SyncOutcome.Failed;
                }

                response.EnsureSuccessStatusCode();
                created = await response.Content
                    .ReadFromJsonAsync<SyncJobResponse>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subsync: failed to submit sync job for {Subtitle}", subtitleFilename);
                return SyncOutcome.SidecarUnreachable;
            }

            if (created is null || string.IsNullOrEmpty(created.JobId))
            {
                _logger.LogError("Subsync: sidecar returned no job id for {Subtitle}", subtitleFilename);
                return SyncOutcome.SidecarUnreachable;
            }

            // The sidecar is the authority on the run budget: it may clamp what
            // was asked for, and a sidecar older than this protocol ignores the
            // field entirely and applies its own. Deriving the deadline from its
            // echo - falling back to our own number only when it says nothing -
            // is what keeps this side's timeout longer than the sidecar's in
            // every combination. That ordering is the whole point: whoever times
            // out second never has to deal with a job the other side is still
            // working on.
            var effectiveTimeout = created.EffectiveTimeoutSeconds is > 0
                ? created.EffectiveTimeoutSeconds.Value
                : requestedTimeout;
            var runBudget = TimeSpan.FromSeconds(effectiveTimeout)
                + TimeSpan.FromSeconds(Math.Max(ClientGraceSeconds, 2 * pollInterval.TotalSeconds));
            var queueBudget = config.QueueWaitTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(config.QueueWaitTimeoutSeconds)
                : (TimeSpan?)null;

            var submittedAt = _timeProvider.GetUtcNow();
            DateTimeOffset? firstSeenRunningAt = null;
            var consecutiveFailures = 0;

            // The first poll comes quickly: a subtitle aligned against an
            // already-synced sibling can finish well inside one poll interval,
            // and paying the full interval per file adds up across a sweep.
            var delay = TimeSpan.FromMilliseconds(Math.Min(250, pollInterval.TotalMilliseconds));

            while (true)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                delay = pollInterval;

                JobStatusResponse? status;
                try
                {
                    using var http = CreateClient(config);
                    using var response = await http.GetAsync($"{baseUrl}/jobs/{created.JobId}", cancellationToken)
                        .ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Terminal, not transient. The sidecar's job table lives
                        // in memory, so a 404 means it restarted or retired the
                        // entry - this job id will never resolve. Treating it as
                        // a retryable blip, as this used to, burned the entire
                        // job budget re-asking a question that already had its
                        // final answer.
                        _logger.LogError(
                            "Subsync: the sidecar no longer knows job {JobId} for {Subtitle} (it most likely "
                            + "restarted); giving up on this file, the next sweep will retry it",
                            created.JobId, subtitleFilename);
                        return SyncOutcome.JobUnknown;
                    }

                    response.EnsureSuccessStatusCode();
                    status = await response.Content
                        .ReadFromJsonAsync<JobStatusResponse>(cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException("the sidecar returned an empty job status");

                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (++consecutiveFailures >= MaxConsecutivePollFailures)
                    {
                        _logger.LogError(
                            ex,
                            "Subsync: {Count} consecutive polling failures for job {JobId} ({Subtitle}); "
                            + "giving up on this file",
                            consecutiveFailures, created.JobId, subtitleFilename);
                        await TryCancelAsync(config, baseUrl, created.JobId, subtitleFilename).ConfigureAwait(false);
                        return SyncOutcome.SidecarUnreachable;
                    }

                    _logger.LogWarning(
                        ex,
                        "Subsync: polling job {JobId} failed ({Count}/{Max}), retrying",
                        created.JobId, consecutiveFailures, MaxConsecutivePollFailures);
                    continue;
                }

                switch (status.Status)
                {
                    case "done":
                        _logger.LogInformation("Subsync: synced {Subtitle}", subtitleFilename);
                        return SyncOutcome.Synced;

                    case "failed":
                        _logger.LogError(
                            "Subsync: sync failed for {Subtitle}: {Error}", subtitleFilename, status.Error);
                        return SyncOutcome.Failed;

                    case "cancelled":
                        _logger.LogWarning(
                            "Subsync: the sidecar reported job {JobId} for {Subtitle} as cancelled",
                            created.JobId, subtitleFilename);
                        return SyncOutcome.Failed;
                }

                if (firstSeenRunningAt is null
                    && (!string.Equals(status.Status, "queued", StringComparison.Ordinal)
                        || status.RunningSeconds is not null))
                    firstSeenRunningAt = _timeProvider.GetUtcNow();

                if (firstSeenRunningAt is null)
                {
                    // Still queued. Queue time is the sidecar's backlog, not
                    // this job's work: a job sitting behind seven others hasn't
                    // spent a second of its run budget, and charging it - as
                    // this used to - meant a busy sidecar produced timeouts on
                    // jobs that had never started, whose results then landed
                    // anyway.
                    if (queueBudget is not null && _timeProvider.GetUtcNow() - submittedAt > queueBudget)
                    {
                        _logger.LogError(
                            "Subsync: job {JobId} for {Subtitle} was still queued after {Seconds}s, cancelling it. "
                            + "Check that Max parallel jobs isn't set well above the sidecar's MAX_PARALLEL_JOBS",
                            created.JobId, subtitleFilename, config.QueueWaitTimeoutSeconds);
                        await TryCancelAsync(config, baseUrl, created.JobId, subtitleFilename).ConfigureAwait(false);
                        return SyncOutcome.QueueTimedOut;
                    }
                }
                else
                {
                    // Prefer the sidecar's own measurement: it's immune to clock
                    // skew between the two containers and to how coarse polling is.
                    var elapsed = status.RunningSeconds is { } serverSeconds
                        ? TimeSpan.FromSeconds(serverSeconds)
                        : _timeProvider.GetUtcNow() - firstSeenRunningAt.Value;

                    if (elapsed > runBudget)
                    {
                        _logger.LogError(
                            "Subsync: job {JobId} for {Subtitle} ran past {Seconds}s without finishing, cancelling it "
                            + "so it can't replace the subtitle after we've stopped tracking it",
                            created.JobId, subtitleFilename, effectiveTimeout);
                        await TryCancelAsync(config, baseUrl, created.JobId, subtitleFilename).ConfigureAwait(false);
                        return SyncOutcome.RunTimedOut;
                    }
                }
            }
        }

        /// <summary>
        /// A fresh client per request, on purpose. IHttpClientFactory rotates
        /// the underlying handler on a lifetime so DNS changes get picked up -
        /// holding one HttpClient in a singleton's field, as this class used to,
        /// pins one handler for the life of the server and defeats that, so a
        /// sidecar container recreated with a new IP was never reached again.
        /// The HttpClient wrapper is cheap; the handler underneath is pooled.
        /// </summary>
        private HttpClient CreateClient(PluginConfiguration config)
        {
            var http = _httpClientFactory.CreateClient(nameof(SubsyncClient));
            // Assigning Timeout is only legal because this instance is brand new
            // and no request has started on it - HttpClient throws once one has.
            // It's per HTTP call, not per job: a poll hanging on a half-open
            // connection would otherwise sit there for HttpClient's 100s default.
            http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.SidecarRequestTimeoutSeconds, 1, 300));
            return http;
        }

        /// <summary>
        /// Tells the sidecar we've stopped caring about a job. A queued one is
        /// dropped outright; a running one is flagged so its result is discarded
        /// rather than written over the subtitle - which is the failure this
        /// exists for. An abandoned job that later overwrote the file left
        /// content nothing had recorded as synced, so every subsequent sweep
        /// synced it again, forever.
        /// </summary>
        /// <remarks>
        /// Best effort by design, and never allowed to change the outcome: it
        /// runs after we've already given up, and a sidecar older than this
        /// endpoint answers 404 or 405. It also deliberately takes no
        /// CancellationToken - a cancelled sweep is exactly when releasing
        /// in-flight jobs matters most.
        /// </remarks>
        private async Task TryCancelAsync(PluginConfiguration config, string baseUrl, string jobId, string subtitleFilename)
        {
            try
            {
                using var http = CreateClient(config);
                using var response = await http.PostAsync($"{baseUrl}/jobs/{jobId}/cancel", content: null)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Subsync: the sidecar answered {Status} when cancelling job {JobId}; if it predates "
                        + "3.0.0.0 it has no cancel endpoint and the job may still replace {Subtitle}",
                        (int)response.StatusCode, jobId, subtitleFilename);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Subsync: couldn't cancel job {JobId}", jobId);
            }
        }

        private sealed record SyncRequest(
            [property: JsonPropertyName("folder")] string Folder,
            [property: JsonPropertyName("reference_filename")] string ReferenceFilename,
            [property: JsonPropertyName("subtitle_filename")] string SubtitleFilename,
            [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds);

        private sealed record SyncJobResponse(
            [property: JsonPropertyName("job_id")] string JobId,
            // Absent from a sidecar older than this protocol.
            [property: JsonPropertyName("effective_timeout_seconds")] int? EffectiveTimeoutSeconds);

        private sealed record JobStatusResponse(
            [property: JsonPropertyName("status")] string Status,
            [property: JsonPropertyName("error")] string? Error,
            // Measured by the sidecar. Null while the job is still queued, and
            // absent entirely from a sidecar older than this protocol.
            [property: JsonPropertyName("running_seconds")] double? RunningSeconds);
    }
}
