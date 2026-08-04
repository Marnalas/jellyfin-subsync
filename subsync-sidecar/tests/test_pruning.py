"""_prune_jobs_locked.

The failure mode this guards is quiet rather than loud: evicting a job before
its owner has polled turns the next poll into a 404, the plugin reads that as
"the sidecar restarted and lost my job", records a failed sync, and re-syncs the
same file on the next sweep. Nothing crashes - the library just churns.
"""
import logging
import time

import pytest

import app


def prune():
    with app.jobs_lock:
        app._prune_jobs_locked()


@pytest.fixture
def small_cap(monkeypatch):
    monkeypatch.setattr(app, "MAX_JOB_HISTORY", 10)


def add(job_id, status, finished_ago=None, queued_ago=0.0):
    now = time.time()
    job = {"status": status, "queued_at": now - queued_ago}
    if finished_ago is not None:
        job["finished_at"] = now - finished_ago
    app.jobs[job_id] = job


def test_expired_terminal_jobs_are_dropped():
    for i in range(5):
        add(f"old{i}", "done", finished_ago=app.JOB_RETENTION_SECONDS + 10)
    prune()
    assert app.jobs == {}


@pytest.mark.parametrize("status", ["done", "failed", "cancelled"])
def test_every_terminal_status_expires(status):
    add("j", status, finished_ago=app.JOB_RETENTION_SECONDS + 10)
    prune()
    assert "j" not in app.jobs


def test_recently_finished_jobs_are_kept():
    add("recent", "done", finished_ago=5)
    prune()
    assert "recent" in app.jobs


@pytest.mark.parametrize("status", ["queued", "running"])
def test_in_flight_jobs_are_never_dropped_by_the_ttl(status):
    """A queued or running job is still owned by a polling client, however long
    it has been sitting there. Evicting it turns that client's next poll into a
    404, so it would abandon a job that was about to succeed."""
    add("live", status, queued_ago=app.JOB_RETENTION_SECONDS * 10)
    prune()
    assert "live" in app.jobs


@pytest.mark.parametrize("status", ["queued", "running"])
def test_status_not_age_decides_what_the_ttl_drops(status):
    """Pins the status guard specifically. A missing finished_at already makes a
    job look brand new to the age check, so that check alone can't distinguish
    the two - carrying a stale finished_at is the only state where the guard is
    the thing doing the work."""
    add("live", status, finished_ago=app.JOB_RETENTION_SECONDS + 10)
    prune()
    assert "live" in app.jobs


def test_over_cap_eviction_takes_the_oldest_first(small_cap):
    for i in range(20):
        add(f"done{i}", "done", finished_ago=1000 - i)
    prune()
    assert len(app.jobs) <= app.MAX_JOB_HISTORY
    assert "done0" not in app.jobs      # oldest
    assert "done19" in app.jobs         # newest


def test_over_cap_eviction_spares_in_flight_jobs(small_cap):
    for i in range(20):
        add(f"done{i}", "done", finished_ago=1000 - i)
    for i in range(5):
        add(f"live{i}", "running")
    prune()
    assert [k for k in app.jobs if k.startswith("live")] == [f"live{i}" for i in range(5)]


def test_the_cap_never_evicts_a_job_that_just_finished(small_cap, caplog):
    """However far over the cap we are, a job that finished seconds ago has
    almost certainly not been polled yet - so a burst of 21 quick syncs against
    a cap of 10 stays over the cap rather than 404-ing eleven live clients.

    Every job here is inside MIN_JOB_RETENTION_SECONDS, which is what makes the
    floor the only thing standing between them and eviction.
    """
    for i in range(21):
        add(f"done{i}", "done", finished_ago=i % app.MIN_JOB_RETENTION_SECONDS)
    with caplog.at_level(logging.WARNING):
        prune()
    assert len(app.jobs) == 21
    assert "too recently finished to evict" in caplog.text


def test_staying_over_the_cap_is_reported(small_cap, caplog):
    """When the excess is all in-flight there is nothing to evict, and silently
    exceeding a configured cap should be visible in the logs."""
    for i in range(15):
        add(f"live{i}", "running")
    with caplog.at_level(logging.WARNING):
        prune()
    assert "MAX_JOB_HISTORY" in caplog.text
    assert len(app.jobs) == 15


def test_pruning_runs_on_submission(client, small_cap):
    """/sync is the only thing that triggers a prune, so an idle sidecar never
    grows and a busy one cleans up as it goes."""
    add("stale", "done", finished_ago=app.JOB_RETENTION_SECONDS + 10)
    client.post("/sync", json={"folder": "/media", "reference_filename": "v.mkv", "subtitle_filename": "s.srt"})
    assert "stale" not in app.jobs
