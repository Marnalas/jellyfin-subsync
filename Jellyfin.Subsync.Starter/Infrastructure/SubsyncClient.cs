using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Subsync.Starter.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    public class SubsyncClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        private readonly HttpClient _http = httpClientFactory.CreateClient(nameof(SubsyncClient));
        private readonly ILogger _logger = logger;

        private static PluginConfiguration Config => Plugin.Instance!.Configuration;

        /// <summary>
        /// Submits a sync job for the given files (already sidecar-relative)
        /// and waits (polling) until it finishes, fails, or times out.
        /// </summary>
        public async Task<bool> SyncAndWaitAsync(
            string folder,
            string referenceFilename,
            string subtitleFilename,
            CancellationToken cancellationToken)
        {
            var baseUrl = Config.SidecarUrl.TrimEnd('/');

            SyncJobResponse? created;
            try
            {
                var response = await _http.PostAsJsonAsync(
                    $"{baseUrl}/sync",
                    new SyncRequest(folder, referenceFilename, subtitleFilename),
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                created = await response.Content.ReadFromJsonAsync<SyncJobResponse>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subsync: failed to submit sync job for {Subtitle}", subtitleFilename);
                return false;
            }

            if (created is null)
            {
                _logger.LogError("Subsync: sidecar returned no job id for {Subtitle}", subtitleFilename);
                return false;
            }

            var deadline = DateTime.UtcNow.AddSeconds(Config.JobTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(Config.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);

                JobStatusResponse? status;
                try
                {
                    status = await _http.GetFromJsonAsync<JobStatusResponse>(
                        $"{baseUrl}/jobs/{created.JobId}", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subsync: polling job {JobId} failed, retrying", created.JobId);
                    continue;
                }

                if (status is null)
                {
                    continue;
                }

                switch (status.Status)
                {
                    case "done":
                        _logger.LogInformation("Subsync: synced {Subtitle}", subtitleFilename);
                        return true;
                    case "failed":
                        _logger.LogError("Subsync: sync failed for {Subtitle}: {Error}", subtitleFilename, status.Error);
                        return false;
                }
                // queued / running -> keep polling
            }

            _logger.LogError("Subsync: timed out waiting for sync of {Subtitle}", subtitleFilename);
            return false;
        }

        private sealed record SyncRequest(
            [property: JsonPropertyName("folder")] string Folder,
            [property: JsonPropertyName("reference_filename")] string ReferenceFilename,
            [property: JsonPropertyName("subtitle_filename")] string SubtitleFilename);

        private sealed record SyncJobResponse([property: JsonPropertyName("job_id")] string JobId);

        private sealed record JobStatusResponse(
            [property: JsonPropertyName("status")] string Status,
            [property: JsonPropertyName("error")] string? Error);
    }
}
