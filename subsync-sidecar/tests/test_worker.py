"""The claim/cancel handoff, and one run through the whole thing.

_claim_job is where a cancel either wins outright or is deferred to the
pre-replace check in _run_ffsubsync. Both halves of that are silent when broken:
the job still reports a status, it's just the wrong one, and the subtitle gets
rewritten anyway.
"""
import time

import app
from conftest import names


def test_claiming_a_queued_job_marks_it_running(make_job):
    job_id = make_job(status="queued", request={"folder": "/media"}, timeout_seconds=42)
    claimed = app._claim_job(job_id)
    assert claimed == ({"folder": "/media"}, 42)
    assert app.jobs[job_id]["status"] == "running"
    assert app.jobs[job_id]["started_at"] is not None


def test_a_cancelled_job_is_never_claimed(make_job):
    """Cancel arrived while the job sat in the queue: the worker pops it, sees
    the flag, and retires it without running ffsubsync at all."""
    job_id = make_job(status="queued", cancel_requested=True)
    assert app._claim_job(job_id) is None
    assert app.jobs[job_id]["status"] == "cancelled"
    assert "finished_at" in app.jobs[job_id]


def test_an_already_cancelled_status_is_not_reclaimed(make_job):
    job_id = make_job(status="cancelled", finished_at=time.time())
    assert app._claim_job(job_id) is None
    assert app.jobs[job_id]["status"] == "cancelled"


def test_a_job_evicted_before_it_ran_is_not_claimed():
    assert app._claim_job("dropped-by-a-prune") is None


def test_terminate_never_downgrades_a_cancelled_job(make_job):
    """A cancel that lands while the job was finishing always wins: the caller
    has stopped waiting, and reporting `done` to nobody would only leave a job
    under a status that misdescribes what happened."""
    job_id = make_job(status="cancelled", finished_at=time.time())
    app._terminate(job_id, "done")
    assert app.jobs[job_id]["status"] == "cancelled"


def test_terminate_ignores_a_job_that_is_already_gone():
    app._terminate("pruned-mid-run", "done")  # must not raise
    assert "pruned-mid-run" not in app.jobs


def test_cancelling_between_claim_and_replace_discards_the_result(client, library, fake_ffsubsync, make_job):
    """The race the single-lock claim exists for: the worker has the job in
    `running`, the plugin gives up, and the cancel has to reach the pre-replace
    check rather than being lost."""
    job_id = make_job(status="queued", timeout_seconds=10)
    app._claim_job(job_id)                                  # worker claims it
    assert client.post(f"/jobs/{job_id}/cancel").json()["status"] == "running"

    request = app.SyncRequest(folder=str(library), reference_filename="v.mkv",
                              subtitle_filename="s.srt", timeout_seconds=10)
    app._run_ffsubsync(job_id, request, 10)

    assert app.jobs[job_id]["status"] == "cancelled"
    assert (library / "s.srt").read_bytes() == b"subs"
    assert names(library) == ["s.srt", "v.mkv"]


def test_end_to_end_through_the_running_app(worker_client, library, fake_ffsubsync):
    """POST /sync, let a real worker thread pick it up, poll until it's over -
    the same sequence the plugin performs."""
    job_id = worker_client.post("/sync", json={
        "folder": str(library),
        "reference_filename": "v.mkv",
        "subtitle_filename": "s.srt",
        "timeout_seconds": 30,
    }).json()["job_id"]

    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        body = worker_client.get(f"/jobs/{job_id}").json()
        if body["status"] in app.TERMINAL_STATUSES:
            break
        time.sleep(0.05)

    assert body["status"] == "done", body
    assert body["running_seconds"] >= 0
    assert (library / "s.srt").read_bytes() == b"SYNCED"
    assert names(library) == ["s.srt", "v.mkv"]


def test_the_pool_stops_when_the_app_shuts_down(library, fake_ffsubsync, monkeypatch):
    """Otherwise a lifespan-started worker outlives its app and keeps draining a
    queue nobody is feeding on purpose."""
    monkeypatch.setattr(app, "MAX_PARALLEL_JOBS", 2)
    app.start_workers()
    assert len(app._worker_threads) == 2
    threads = list(app._worker_threads)

    app.stop_workers(timeout=5.0)
    assert app._worker_threads == []
    assert not any(t.is_alive() for t in threads)
