using System.Net;
using System.Text.Json;
using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Infrastructure;
using Jellyfin.Subsync.Starter.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests;

/// <summary>
/// Covers the plugin's half of the sidecar protocol: what it sends, how it
/// decides a job is over, and what it does when the sidecar stops
/// cooperating. The clock is injected, so the timeout cases run instantly
/// and deterministically instead of taking the wall-clock time they name.
/// </summary>
public class SubsyncClientTests
{
    private readonly ManualTimeProvider _time = new();

    private static PluginConfiguration Config(
        int jobTimeoutSeconds = 1800,
        int queueWaitTimeoutSeconds = 3600,
        int pollIntervalMilliseconds = 1000) => new()
    {
        SidecarUrl = "http://sidecar:8000",
        JobTimeoutSeconds = jobTimeoutSeconds,
        QueueWaitTimeoutSeconds = queueWaitTimeoutSeconds,
        PollIntervalMilliseconds = pollIntervalMilliseconds
    };

    private static HttpResponseMessage Created(string jobId = "job-1", int? effectiveTimeoutSeconds = null) =>
        FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            effectiveTimeoutSeconds is null
                ? $$"""{"job_id":"{{jobId}}"}"""
                : $$"""{"job_id":"{{jobId}}","effective_timeout_seconds":{{effectiveTimeoutSeconds}}}""");

    private static HttpResponseMessage JobStatus(string status, double? runningSeconds = null, string? error = null)
    {
        var running = runningSeconds is null ? "null" : runningSeconds.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var errorJson = error is null ? "null" : JsonSerializer.Serialize(error);
        return FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"job_id":"job-1","status":"{{status}}","error":{{errorJson}},"running_seconds":{{running}}}""");
    }

    private (SubsyncClient Client, FakeHttpMessageHandler Handler, FakeHttpClientFactory Factory) Build(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpMessageHandler(respond);
        var factory = new FakeHttpClientFactory(handler);
        return (new SubsyncClient(factory, NullLogger.Instance, _time), handler, factory);
    }

    /// <summary>
    /// Lets the code under test run until it is waiting on the clock again.
    /// Advancing before it has scheduled its next delay would silently skip
    /// a poll and make these tests lie.
    /// </summary>
    private async Task SettleAsync(Task task)
    {
        for (var i = 0; i < 500; i++)
        {
            if (task.IsCompleted || _time.HasPendingTimer)
                return;

            if (i < 100)
                await Task.Yield();
            else
                await Task.Delay(1);
        }
    }

    private async Task AdvanceAsync(Task task, TimeSpan step, int steps)
    {
        for (var i = 0; i < steps && !task.IsCompleted; i++)
        {
            await SettleAsync(task);
            if (task.IsCompleted)
                return;

            _time.Advance(step);
        }

        await SettleAsync(task);
    }

    private async Task<T> PumpToCompletionAsync<T>(Task<T> task, TimeSpan step, int maxSteps = 20_000)
    {
        await AdvanceAsync(task, step, maxSteps);
        Assert.True(task.IsCompleted, "the client never reached a terminal state");
        return await task;
    }

    private Task<SyncOutcome> StartSync(SubsyncClient client, PluginConfiguration config) =>
        client.SyncAndWaitAsync(config, "/media/films/Movie", "Movie.mkv", "Movie.en.srt", CancellationToken.None);

    [Fact]
    public async Task Submit_SendsTheRunBudgetSoTheSidecarEnforcesIt()
    {
        var (client, handler, _) = Build((request, _) =>
            request.RequestUri!.AbsolutePath == "/sync" ? Created() : JobStatus("done"));

        var outcome = await PumpToCompletionAsync(StartSync(client, Config(jobTimeoutSeconds: 900)), TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.Synced, outcome);

        var submitted = JsonSerializer.Deserialize<JsonElement>(handler.Requests[0].Body!);
        Assert.Equal(900, submitted.GetProperty("timeout_seconds").GetInt32());
        Assert.Equal("/media/films/Movie", submitted.GetProperty("folder").GetString());
        Assert.Equal("Movie.mkv", submitted.GetProperty("reference_filename").GetString());
        Assert.Equal("Movie.en.srt", submitted.GetProperty("subtitle_filename").GetString());
    }

    [Fact]
    public async Task DoneJob_IsSynced()
    {
        var (client, _, _) = Build((request, ordinal) => request.RequestUri!.AbsolutePath == "/sync"
            ? Created()
            : ordinal < 3 ? JobStatus("running", runningSeconds: ordinal) : JobStatus("done", runningSeconds: 4));

        Assert.Equal(SyncOutcome.Synced, await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task FailedJob_IsFailed()
    {
        var (client, _, _) = Build((request, _) => request.RequestUri!.AbsolutePath == "/sync"
            ? Created()
            : JobStatus("failed", error: "ffsubsync exited 1"));

        Assert.Equal(SyncOutcome.Failed, await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The regression test for the restarted-sidecar stall: a 404 means the
    /// in-memory job table is gone and the id will never resolve, so it has
    /// to end the wait immediately rather than be retried for the full job
    /// timeout.
    /// </summary>
    [Fact]
    public async Task UnknownJob_IsTerminalAndStopsPollingImmediately()
    {
        var (client, handler, _) = Build((request, _) => request.RequestUri!.AbsolutePath == "/sync"
            ? Created()
            : FakeHttpMessageHandler.Status(HttpStatusCode.NotFound));

        var outcome = await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.JobUnknown, outcome);
        Assert.Equal(2, handler.Requests.Count);   // the submit, and exactly one poll
    }

    [Fact]
    public async Task RepeatedPollFailures_GiveUpAtTheCapAndCancelTheJob()
    {
        var (client, handler, _) = Build((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sync")
                return Created();
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"job_id":"job-1","status":"cancelled","cancelled":true}""");
            throw new HttpRequestException("connection refused");
        });

        var outcome = await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.SidecarUnreachable, outcome);
        Assert.Equal(5, handler.Requests.Count(r => r.Path == "/jobs/job-1"));
        Assert.Contains(handler.Requests, r => r.Path == "/jobs/job-1/cancel");
    }

    [Fact]
    public async Task PollFailureCap_IsConsecutiveNotCumulative()
    {
        var failuresBeforeSuccess = new[] { 1, 2, 3, 4, 6, 7, 8, 9 };
        var (client, handler, _) = Build((request, ordinal) =>
        {
            if (request.RequestUri!.AbsolutePath == "/sync")
                return Created();
            if (failuresBeforeSuccess.Contains(ordinal))
                throw new HttpRequestException("connection refused");
            return ordinal >= 10 ? JobStatus("done") : JobStatus("running", runningSeconds: 1);
        });

        var outcome = await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.Synced, outcome);
        Assert.True(handler.Requests.Count > 6, "the run should have survived more failures than the cap allows in a row");
    }

    /// <summary>
    /// The regression test for the orphaned-job loop: time a job spends
    /// queued is the sidecar's backlog, not this job's work. Charging it
    /// against the run budget - as this used to - made a busy sidecar
    /// produce timeouts on jobs that had never started, whose results then
    /// overwrote the subtitle anyway.
    /// </summary>
    [Fact]
    public async Task QueueTime_IsNotChargedAgainstTheRunBudget()
    {
        var (client, handler, _) = Build((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sync")
                return Created();
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"job_id":"job-1","status":"cancelled","cancelled":true}""");
            return JobStatus("queued");
        });

        var config = Config(jobTimeoutSeconds: 60, queueWaitTimeoutSeconds: 600);
        var task = StartSync(client, config);

        // Five minutes queued: far past the 60s run budget, well inside the
        // 600s queue budget. The job hasn't started, so nothing has expired.
        await AdvanceAsync(task, TimeSpan.FromSeconds(1), 300);
        Assert.False(task.IsCompleted, "a job that is still queued must not be timed out on its run budget");

        var outcome = await PumpToCompletionAsync(task, TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.QueueTimedOut, outcome);
        Assert.Contains(handler.Requests, r => r.Path == "/jobs/job-1/cancel");
    }

    [Fact]
    public async Task QueueWaitTimeoutOfZero_WaitsIndefinitely()
    {
        var (client, _, _) = Build((request, ordinal) => request.RequestUri!.AbsolutePath == "/sync"
            ? Created()
            : ordinal < 500 ? JobStatus("queued") : JobStatus("done"));

        var config = Config(jobTimeoutSeconds: 60, queueWaitTimeoutSeconds: 0);
        var task = StartSync(client, config);

        await AdvanceAsync(task, TimeSpan.FromSeconds(1), 400);
        Assert.False(task.IsCompleted);

        Assert.Equal(SyncOutcome.Synced, await PumpToCompletionAsync(task, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The client's deadline has to follow the timeout the sidecar says it
    /// will actually apply, not the one that was asked for - that echo is
    /// what keeps the client's budget the longer of the two, so the sidecar
    /// is always the side that declares a timeout.
    /// </summary>
    [Fact]
    public async Task RunBudget_FollowsTheTimeoutTheSidecarEchoesBack()
    {
        var (client, handler, _) = Build((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sync")
                return Created(effectiveTimeoutSeconds: 60);   // clamped, well below what was asked
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"job_id":"job-1","status":"running","cancelled":false}""");
            return JobStatus("running", runningSeconds: null);
        });

        var start = _time.GetUtcNow();
        var outcome = await PumpToCompletionAsync(StartSync(client, Config(jobTimeoutSeconds: 3600)), TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.RunTimedOut, outcome);
        // 60s clamped budget + the 60s grace, not the 3600s that was asked for.
        Assert.True(
            _time.GetUtcNow() - start < TimeSpan.FromSeconds(200),
            $"gave up after {_time.GetUtcNow() - start}, which means it used its own timeout rather than the sidecar's");
        Assert.Contains(handler.Requests, r => r.Path == "/jobs/job-1/cancel");
    }

    [Fact]
    public async Task RunBudget_IsMeasuredFromTheSidecarsOwnElapsedTime()
    {
        // The sidecar reports elapsed time running far ahead of the client's
        // clock - a clock-skew stand-in. Its number is the one that counts.
        var (client, _, _) = Build((request, ordinal) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sync")
                return Created(effectiveTimeoutSeconds: 600);
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"job_id":"job-1","status":"running","cancelled":false}""");
            return JobStatus("running", runningSeconds: ordinal * 100.0);
        });

        var start = _time.GetUtcNow();
        var outcome = await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.RunTimedOut, outcome);
        Assert.True(
            _time.GetUtcNow() - start < TimeSpan.FromSeconds(60),
            "the server's elapsed time should have tripped the budget long before the client's own clock did");
    }

    /// <summary>
    /// A sidecar older than this protocol answers without
    /// effective_timeout_seconds or running_seconds. Everything still has
    /// to work; only the extra precision is lost.
    /// </summary>
    [Fact]
    public async Task OlderSidecarWithoutTheNewFields_StillCompletes()
    {
        var (client, _, _) = Build((request, ordinal) => request.RequestUri!.AbsolutePath == "/sync"
            ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"job_id":"job-1"}""")
            : FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                ordinal < 3 ? """{"status":"running"}""" : """{"status":"done"}"""));

        Assert.Equal(SyncOutcome.Synced, await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancelEndpointMissing_DoesNotChangeTheOutcome()
    {
        var (client, _, _) = Build((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sync")
                return Created();
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
                return FakeHttpMessageHandler.Status(HttpStatusCode.MethodNotAllowed);   // sidecar predates it
            return JobStatus("queued");
        });

        var outcome = await PumpToCompletionAsync(
            StartSync(client, Config(queueWaitTimeoutSeconds: 30)), TimeSpan.FromSeconds(1));

        Assert.Equal(SyncOutcome.QueueTimedOut, outcome);
    }

    [Fact]
    public async Task RejectedSubmission_IsFailedNotUnreachable()
    {
        var (client, _, _) = Build((_, _) => FakeHttpMessageHandler.Status(HttpStatusCode.UnprocessableEntity));

        var outcome = await StartSync(client, Config());

        // The sidecar answered - it just refused. Re-sending the same
        // request won't help, and it says nothing about the sidecar's health.
        Assert.Equal(SyncOutcome.Failed, outcome);
    }

    [Fact]
    public async Task UnreachableSidecarOnSubmit_IsUnreachable()
    {
        var (client, _, _) = Build((_, _) => throw new HttpRequestException("no route to host"));

        Assert.Equal(SyncOutcome.SidecarUnreachable, await StartSync(client, Config()));
    }

    /// <summary>
    /// Pins the handler-rotation fix: the client has to ask the factory for
    /// a client per call. Caching one in a field kept a single handler - and
    /// with it a single resolved IP - for the life of the server, so a
    /// recreated sidecar container was never reached again.
    /// </summary>
    [Fact]
    public async Task EveryHttpCall_AsksTheFactoryForAClient()
    {
        var (client, handler, factory) = Build((request, ordinal) => request.RequestUri!.AbsolutePath == "/sync"
            ? Created()
            : ordinal < 4 ? JobStatus("running", runningSeconds: ordinal) : JobStatus("done"));

        await PumpToCompletionAsync(StartSync(client, Config()), TimeSpan.FromSeconds(1));

        Assert.Equal(handler.Requests.Count, factory.CreateCount);
        Assert.True(factory.CreateCount > 1);
    }

    [Fact]
    public async Task IsHealthy_IsTrueOnlyWhenTheSidecarAnswersSuccessfully()
    {
        var (ok, _, _) = Build((_, _) => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        Assert.True(await ok.IsHealthyAsync(Config(), CancellationToken.None));

        var (broken, _, _) = Build((_, _) => FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        Assert.False(await broken.IsHealthyAsync(Config(), CancellationToken.None));

        var (down, _, _) = Build((_, _) => throw new HttpRequestException("connection refused"));
        Assert.False(await down.IsHealthyAsync(Config(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_IsPropagatedRatherThanSwallowedAsAPollFailure()
    {
        using var cts = new CancellationTokenSource();
        var (client, _, _) = Build((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/sync")
                cts.Cancel();
            return request.RequestUri.AbsolutePath == "/sync" ? Created() : JobStatus("running", runningSeconds: 1);
        });

        var task = client.SyncAndWaitAsync(Config(), "/f", "v.mkv", "s.srt", cts.Token);
        await AdvanceAsync(task, TimeSpan.FromSeconds(1), 20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}