"""The HTTP surface, exercised through a real TestClient.

No worker pool is running (see conftest), so a job posted here stays queued and
its bookkeeping can be inspected without racing anything.
"""
import time

import app

SYNC_BODY = {"folder": "/media/library1/Show", "reference_filename": "v.mkv", "subtitle_filename": "s.srt"}


def test_health(client):
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_sync_queues_a_job(client):
    response = client.post("/sync", json=SYNC_BODY)
    assert response.status_code == 200
    job_id = response.json()["job_id"]
    assert app.jobs[job_id]["status"] == "queued"
    assert app.job_queue.qsize() == 1


def test_sync_echoes_the_effective_timeout(client):
    """The echo is what lets the plugin set its own deadline from what will
    actually happen rather than from what it asked for."""
    response = client.post("/sync", json={**SYNC_BODY, "timeout_seconds": 999_999})
    assert response.json()["effective_timeout_seconds"] == app.MAX_JOB_TIMEOUT_SECONDS


def test_sync_without_a_timeout_falls_back_to_the_default(client):
    """What a plugin older than 3.0.0.0 sends."""
    response = client.post("/sync", json=SYNC_BODY)
    assert response.json()["effective_timeout_seconds"] == app.JOB_TIMEOUT_SECONDS


def test_sync_rejects_a_malformed_body(client):
    response = client.post("/sync", json={"folder": "/media"})
    assert response.status_code == 422


def test_unknown_job_is_404(client):
    """The plugin treats this as terminal - "the sidecar restarted and lost my
    job" - so it has to stay a 404 and not, say, an empty 200."""
    assert client.get("/jobs/does-not-exist").status_code == 404


def test_get_job_reports_running_seconds(client, make_job):
    job_id = make_job(status="running", started_at=time.time() - 5)
    running_seconds = client.get(f"/jobs/{job_id}").json()["running_seconds"]
    assert 4.5 < running_seconds < 6.5


def test_running_seconds_is_null_while_queued(client, make_job):
    job_id = make_job(status="queued", started_at=None)
    assert client.get(f"/jobs/{job_id}").json()["running_seconds"] is None


def test_running_seconds_freezes_when_the_job_finishes(client, make_job):
    """Measured server-side because the two containers' clocks need not agree,
    and this is the number the client charges against its run budget."""
    started = time.time() - 30
    job_id = make_job(status="done", started_at=started, finished_at=started + 12)
    assert client.get(f"/jobs/{job_id}").json()["running_seconds"] == 12


def test_cancel_a_queued_job_drops_it(client):
    job_id = client.post("/sync", json=SYNC_BODY).json()["job_id"]
    body = client.post(f"/jobs/{job_id}/cancel").json()
    assert body["status"] == "cancelled"
    assert body["cancelled"] is True
    assert "finished_at" in app.jobs[job_id]


def test_cancel_a_running_job_only_flags_it(client, make_job):
    """ffsubsync is left to finish into its temp file - cheaper and safer than
    interrupting it mid-write - and the result is discarded instead."""
    job_id = make_job(status="running")
    body = client.post(f"/jobs/{job_id}/cancel").json()
    assert body["status"] == "running"
    assert body["cancelled"] is False
    assert app.jobs[job_id]["cancel_requested"] is True


def test_cancel_a_finished_job_is_not_an_error(client, make_job):
    """The client calls this after it has already given up, and has nothing
    useful to do with a failure here."""
    job_id = make_job(status="done", finished_at=time.time())
    assert client.post(f"/jobs/{job_id}/cancel").status_code == 200


def test_cancel_an_unknown_job_is_404(client):
    assert client.post("/jobs/does-not-exist/cancel").status_code == 404


def test_list_jobs_is_newest_first(client, make_job):
    now = time.time()
    make_job(status="done", queued_at=now - 30)
    newest = make_job(status="done", queued_at=now)
    make_job(status="done", queued_at=now - 60)
    assert client.get("/jobs").json()[0]["job_id"] == newest


def test_list_jobs_respects_the_limit(client, make_job):
    for _ in range(5):
        make_job(status="done")
    assert len(client.get("/jobs", params={"limit": 2}).json()) == 2
