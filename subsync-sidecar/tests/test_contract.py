"""The half of the sidecar protocol the plugin codes against.

Jellyfin.Subsync.Starter.Tests/SubsyncClientTests.cs hardcodes these response
shapes as fakes - e.g. `{"job_id":"job-1","status":"cancelled","cancelled":true}`
for a cancel. Those fakes pin the plugin's reading of the protocol but say
nothing about the sidecar's writing of it, so without this module a rename here
would leave both suites green and break only production.

If a test in here fails, the matching C# fake needs the same edit.
"""
import time

import app

SYNC_BODY = {"folder": "/media/library1/Show", "reference_filename": "v.mkv", "subtitle_filename": "s.srt"}


def test_sync_response_keys(client):
    """SubsyncClient reads job_id, and effective_timeout_seconds when present."""
    body = client.post("/sync", json={**SYNC_BODY, "timeout_seconds": 600}).json()
    assert set(body) == {"job_id", "effective_timeout_seconds"}
    assert isinstance(body["job_id"], str)
    assert isinstance(body["effective_timeout_seconds"], int)


def test_cancel_response_keys(client, make_job):
    job_id = make_job(status="queued")
    body = client.post(f"/jobs/{job_id}/cancel").json()
    assert set(body) == {"job_id", "status", "cancelled"}
    assert isinstance(body["cancelled"], bool)


def test_job_status_payload_carries_what_the_client_polls_for(client, make_job):
    job_id = make_job(status="running", started_at=time.time())
    body = client.get(f"/jobs/{job_id}").json()
    assert {"job_id", "status", "running_seconds"} <= set(body)


def test_a_failed_job_reports_its_error(client, make_job):
    """The plugin surfaces this string in the Jellyfin task log, so it has to be
    on the job payload rather than only in the sidecar's own logs."""
    job_id = make_job(status="failed", error="ffsubsync exited 3", finished_at=time.time())
    assert client.get(f"/jobs/{job_id}").json()["error"] == "ffsubsync exited 3"


def test_the_status_vocabulary_is_what_the_plugin_switches_on():
    """queued | running | done | failed | cancelled. The plugin decides a job is
    over by matching these, so an added or renamed status is a protocol change.
    """
    assert app.TERMINAL_STATUSES == frozenset({"done", "failed", "cancelled"})
