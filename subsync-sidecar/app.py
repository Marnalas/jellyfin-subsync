"""
subsync-sidecar
================
A minimal, always-on HTTP service that wraps ffsubsync. Runs as a persistent
docker-compose, so the Jellyfin plugin can trigger syncs over the network
without touching the docker socket or the Jellyfin container.

Endpoints:
  GET  /health               -> {"status": "ok"}
  POST /sync                 -> queue a sync job, returns {"job_id": "..."}
  GET  /jobs/{job_id}        -> job status: queued | running | done | failed | cancelled
  POST /jobs/{job_id}/cancel -> stop caring about a job, so its result is discarded
  GET  /jobs                 -> list recent jobs

Jobs are processed by a pool of MAX_PARALLEL_JOBS worker threads.
"""
import os
import shlex
import subprocess
import threading
import queue
import uuid
import time
import logging
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("subsync-sidecar")


def _env_int(name: str, default: int, minimum: int = 1) -> int:
    """Read an int env var, falling back to the default when unset, empty,
    unparseable or below `minimum`. A typo in a compose file should never stop
    the sidecar from starting."""
    raw = os.environ.get(name, "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
    except ValueError:
        log.warning("Ignoring unparseable %s=%r, using %d instead", name, raw, default)
        return default
    if value < minimum:
        log.warning("Ignoring %s=%d, below the minimum of %d; using %d instead", name, value, minimum, default)
        return default
    return value


# no --vad flag, so ffsubsync uses its default (webrtc). Switching to
# GPU-accelerated silero VAD is a later, separate step once this known-working
# baseline is confirmed running.
# shlex.split rather than str.split: with a bare split, an argument containing
# a space (FFSUBSYNC_EXTRA_ARGS='--vad "webrtc x"') arrived as three tokens,
# two of them carrying literal quote characters, and the job failed on an
# argument the user could see was correct. There's no shell=True anywhere here,
# so this is about parsing, not injection.
try:
    FFSUBSYNC_EXTRA_ARGS = shlex.split(os.environ.get("FFSUBSYNC_EXTRA_ARGS", ""))
except ValueError as e:
    log.warning("Ignoring unparseable FFSUBSYNC_EXTRA_ARGS (%s), using none", e)
    FFSUBSYNC_EXTRA_ARGS = []

# ffsubsync only decodes the audio track (via ffmpeg), not the full video, so
# it's light enough per-job to run several at once on a multi-core host.
# Leave one core free by default for the rest of the system (Jellyfin
# transcoding, etc, often shares the same box); override explicitly via
# MAX_PARALLEL_JOBS if that guess is wrong for your setup (e.g. the
# container has a `--cpus` limit lower than the host's core count).
# An unset, "0", or unparseable value all mean "auto-detect", as documented in
# compose.yml - taking int("0") at face value would start zero worker threads,
# leaving every submitted job queued forever with nothing to run it.
_configured_parallel_jobs = _env_int("MAX_PARALLEL_JOBS", 0, minimum=0)
MAX_PARALLEL_JOBS = _configured_parallel_jobs if _configured_parallel_jobs > 0 else max(1, (os.cpu_count() or 1) - 1)

# How long a single ffsubsync run may take when the caller doesn't say. The
# plugin sends its own per-job budget in `timeout_seconds`; this is the fallback
# for a plugin too old to send one.
JOB_TIMEOUT_SECONDS = _env_int("JOB_TIMEOUT_SECONDS", 1800)

# Hard ceiling on what a caller may ask for, so a mistyped plugin setting can't
# pin a worker thread for a day.
MAX_JOB_TIMEOUT_SECONDS = _env_int("MAX_JOB_TIMEOUT_SECONDS", 7200)

# `jobs` is in-memory and would otherwise grow for the life of the container -
# an always-on sidecar chewing through a 50k-subtitle library accumulates
# hundreds of MB it never gives back. Finished jobs are dropped once they're
# older than the TTL, and the total is capped regardless.
#
# The minimum of 60s on the TTL is load-bearing: the plugin treats a 404 from
# /jobs/{id} as terminal ("the sidecar restarted and lost my job"), so a job
# evicted between finishing and being polled would be read as a failed sync and
# re-synced on the next sweep. An hour of retention against a 3-second poll
# interval is three orders of magnitude of margin; anything near the poll
# interval would not be.
JOB_RETENTION_SECONDS = _env_int("JOB_RETENTION_SECONDS", 3600, minimum=60)
MAX_JOB_HISTORY = _env_int("MAX_JOB_HISTORY", 500, minimum=10)

# Same reasoning applied to the over-cap path: a job that finished seconds ago
# is never evicted, however far over the cap we are.
MIN_JOB_RETENTION_SECONDS = 60

# Off by default: the synced subtitle replaces the original in place and no
# copy is kept. Set to "true" to keep a "<name>_original_backup<ext>" copy
# of the pre-sync subtitle alongside it.
KEEP_ORIGINAL_SUBTITLE_BACKUP = os.environ.get("KEEP_ORIGINAL_SUBTITLE_BACKUP", "").strip().lower() in ("1", "true", "yes")

TERMINAL_STATUSES = frozenset(("done", "failed", "cancelled"))

app = FastAPI(title="subsync-sidecar")

job_queue: "queue.Queue[str]" = queue.Queue()
jobs: dict[str, dict] = {}
jobs_lock = threading.Lock()


class SyncRequest(BaseModel):
    folder: str            # absolute, sidecar-side path
    reference_filename: str
    subtitle_filename: str
    # How long the caller is prepared to let this job run. Absent from plugins
    # older than 3.0.0.0, which had no say in it at all; None means
    # JOB_TIMEOUT_SECONDS. Capped by MAX_JOB_TIMEOUT_SECONDS either way.
    timeout_seconds: Optional[int] = None


def _effective_timeout(requested: Optional[int]) -> int:
    if requested is None or requested <= 0:
        requested = JOB_TIMEOUT_SECONDS
    return min(requested, MAX_JOB_TIMEOUT_SECONDS)


def _terminate(job_id: str, status: str, error: Optional[str] = None, **extra):
    """Move a job to a terminal state.

    A cancel that lands while the job was finishing always wins: the caller has
    stopped waiting, and reporting `done` to nobody would only leave a job the
    next prune keeps around under a status that misdescribes what happened.
    """
    with jobs_lock:
        job = jobs.get(job_id)
        if job is None or job.get("status") == "cancelled":
            return
        job["status"] = status
        job["finished_at"] = time.time()
        if error is not None:
            job["error"] = error
        job.update(extra)


def _fail(job_id: str, message: str):
    _terminate(job_id, "failed", error=message)
    log.error("Job %s: %s", job_id, message)


def _run_ffsubsync(job_id: str, req: SyncRequest, timeout_seconds: int):
    folder = Path(req.folder)
    try:
        folder.resolve()
    except ValueError:
        _fail(job_id, f"Can't resolve folder {folder}")
        return

    reference_path = folder / req.reference_filename
    sub_path = folder / req.subtitle_filename

    if not reference_path.is_file():
        _fail(job_id, f"Reference file not found: {reference_path}")
        return
    if not sub_path.is_file():
        _fail(job_id, f"Subtitle file not found: {sub_path}")
        return

    # Keep the original extension (.srt, .ass, .ssa, .vtt, ...) rather than
    # forcing .srt: ffsubsync/pysubs2 pick the output format from the -o
    # extension, so forcing .srt here would silently downconvert formats
    # like ASS to SRT, discarding styling, before renaming the SRT content
    # over the still-.ass-named original.
    sub_ext = sub_path.suffix
    temp_out = sub_path.with_name(sub_path.stem + "_synced_temp" + sub_ext)
    backup_path = sub_path.with_name(sub_path.stem + "_original_backup" + sub_ext) if KEEP_ORIGINAL_SUBTITLE_BACKUP else None

    # A temp file already sitting there is debris from an earlier attempt that
    # died before it could rename (container restart, OOM kill). Nothing else
    # ever cleans those up, so they accumulate in the user's library.
    if temp_out.exists():
        log.warning("Job %s: removing stale temp file %s from an earlier attempt", job_id, temp_out)
        try:
            temp_out.unlink()
        except OSError as e:
            _fail(job_id, f"Can't remove stale temp file {temp_out}: {e}")
            return

    cmd = [
        "ffsubsync",
        str(reference_path),
        "-i", str(sub_path),
        "-o", str(temp_out),
        *FFSUBSYNC_EXTRA_ARGS,
    ]

    log.info("Job %s: running %s (timeout %ds)", job_id, " ".join(cmd), timeout_seconds)
    # The finally is what keeps temp files from piling up: every early return
    # below leaves a partially written one behind otherwise, and the only path
    # that should keep it is the successful replace() - which consumes it.
    try:
        try:
            result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            _fail(job_id, f"ffsubsync timed out after {timeout_seconds}s")
            return
        except OSError as e:
            # Almost always "ffsubsync isn't on PATH" - a broken image rather
            # than a bad subtitle. Named explicitly so the log says so instead
            # of surfacing as a bare unhandled error from the worker.
            _fail(job_id, f"Could not run ffsubsync: {e}")
            return

        if result.returncode != 0 or not temp_out.is_file():
            with jobs_lock:
                # Only kept on failure. This is diagnostic output; a successful
                # run's is never read by anything, and 8 KB per job across a
                # whole library is what made this dict expensive.
                if job_id in jobs:
                    jobs[job_id]["stdout"] = result.stdout[-2000:]
                    jobs[job_id]["stderr"] = result.stderr[-2000:]
            _fail(job_id, f"ffsubsync exited {result.returncode}")
            return

        with jobs_lock:
            cancelled = jobs.get(job_id, {}).get("cancel_requested", False)
        if cancelled:
            # The plugin gave up on this job while it ran, so it will never
            # record the result. Replacing the subtitle now would leave content
            # nothing knows is synced, and every future sweep would sync it
            # again - the exact loop this endpoint exists to prevent.
            _terminate(job_id, "cancelled", error="cancelled by the client before the subtitle was replaced")
            log.info("Job %s: cancelled after running; subtitle left untouched", job_id)
            return

        try:
            if backup_path is not None:
                # Backup original, then replace it with the synced version.
                backup_path.write_bytes(sub_path.read_bytes())
            temp_out.replace(sub_path)
        except OSError as e:
            _fail(job_id, f"Post-processing failed: {e}")
            return

        _terminate(
            job_id, "done",
            backup_path=str(backup_path) if backup_path is not None else None,
            # The tail of stderr is where ffsubsync reports the offset it
            # applied, which is the one line worth keeping from a good run.
            stderr=result.stderr[-1000:],
        )
        log.info("Job %s: done", job_id)
    finally:
        # A successful replace() moved the temp away, so this is a no-op on the
        # happy path and a cleanup on every other one.
        if temp_out.exists():
            try:
                temp_out.unlink()
            except OSError as e:
                log.warning("Job %s: couldn't remove temp file %s: %s", job_id, temp_out, e)


def _prune_jobs_locked():
    """Drop finished jobs that are old enough, or over the cap. Caller holds jobs_lock.

    Only terminal jobs are ever dropped. A queued or running job is still owned
    by a polling client, and evicting it turns that client's next poll into a
    404 - which the plugin now treats as terminal - so it would abandon a job
    that was about to succeed and rewrite the file behind it.
    """
    now = time.time()
    dropped = 0

    for job_id in [
        job_id for job_id, job in jobs.items()
        if job["status"] in TERMINAL_STATUSES
        and now - job.get("finished_at", now) > JOB_RETENTION_SECONDS
    ]:
        del jobs[job_id]
        dropped += 1

    overflow = len(jobs) - MAX_JOB_HISTORY
    if overflow > 0:
        oldest_first = sorted(
            (job.get("finished_at", 0.0), job_id)
            for job_id, job in jobs.items()
            if job["status"] in TERMINAL_STATUSES
            and now - job.get("finished_at", now) > MIN_JOB_RETENTION_SECONDS
        )
        for _, job_id in oldest_first[:overflow]:
            del jobs[job_id]
            dropped += 1

        if len(jobs) > MAX_JOB_HISTORY:
            log.warning(
                "%d jobs retained, above MAX_JOB_HISTORY=%d: the excess is queued, running, or "
                "too recently finished to evict", len(jobs), MAX_JOB_HISTORY)

    if dropped:
        log.debug("Pruned %d finished job(s), %d retained", dropped, len(jobs))


def _worker():
    while True:
        job_id = job_queue.get()
        try:
            with jobs_lock:
                job = jobs.get(job_id)
                if job is None:
                    continue
                if job.get("cancel_requested") or job["status"] != "queued":
                    job["status"] = "cancelled"
                    job["finished_at"] = time.time()
                    log.info("Job %s: cancelled before it started", job_id)
                    continue
                # Claimed under the same lock as the cancel check, so a cancel
                # arriving right now sees "running" and takes the
                # discard-the-result path rather than being overwritten here.
                job["status"] = "running"
                job["started_at"] = time.time()
                req = job["request"]
                timeout_seconds = job["timeout_seconds"]

            try:
                _run_ffsubsync(job_id, SyncRequest(**req), timeout_seconds)
            except Exception as e:  # keep the worker alive no matter what
                _fail(job_id, f"Unhandled error: {e}")
        finally:
            # In a finally because every early continue above would otherwise
            # leak a queue item and desynchronise queue.join() accounting.
            job_queue.task_done()


log.info("Starting %d sync worker thread(s) (MAX_PARALLEL_JOBS)", MAX_PARALLEL_JOBS)
for _ in range(MAX_PARALLEL_JOBS):
    threading.Thread(target=_worker, daemon=True).start()


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/sync")
def create_sync(req: SyncRequest):
    effective = _effective_timeout(req.timeout_seconds)
    job_id = str(uuid.uuid4())
    with jobs_lock:
        _prune_jobs_locked()
        jobs[job_id] = {
            "status": "queued",
            "request": req.model_dump(),
            "queued_at": time.time(),
            "timeout_seconds": effective,
            "cancel_requested": False,
        }
    job_queue.put(job_id)
    # The effective timeout is echoed so the client can set its own deadline
    # from what will actually happen rather than from what it asked for. That
    # echo is the whole reason the client's timeout is reliably the longer of
    # the two, which is what stops it abandoning a job that's still running.
    return {"job_id": job_id, "effective_timeout_seconds": effective}


@app.get("/jobs/{job_id}")
def get_job(job_id: str):
    with jobs_lock:
        job = jobs.get(job_id)
        if not job:
            raise HTTPException(status_code=404, detail="job not found")
        payload = dict(job)
        started_at = job.get("started_at")
        # Measured here rather than left for the client to subtract timestamps:
        # the two containers' clocks need not agree, and this is the number the
        # client charges against its run budget.
        payload["running_seconds"] = (
            max(0.0, (job.get("finished_at") or time.time()) - started_at) if started_at else None
        )
    return {"job_id": job_id, **payload}


@app.post("/jobs/{job_id}/cancel")
def cancel_job(job_id: str):
    """Tell the sidecar nobody is waiting for this job any more.

    A queued job is dropped outright. A running one is flagged, and its result
    is discarded instead of replacing the subtitle - ffsubsync is left to finish
    into its temp file, which is cheaper and safer than interrupting it
    mid-write. Never an error for a job that has already finished: the client
    calls this after it has given up and has nothing useful to do with a
    failure here.
    """
    with jobs_lock:
        job = jobs.get(job_id)
        if not job:
            raise HTTPException(status_code=404, detail="job not found")
        job["cancel_requested"] = True
        if job["status"] == "queued":
            job["status"] = "cancelled"
            job["finished_at"] = time.time()
        status = job["status"]
    log.info("Job %s: cancel requested (status now %s)", job_id, status)
    return {"job_id": job_id, "status": status, "cancelled": status == "cancelled"}


@app.get("/jobs")
def list_jobs(limit: int = 50):
    with jobs_lock:
        items = sorted(jobs.items(), key=lambda kv: kv[1].get("queued_at", 0), reverse=True)
    return [{"job_id": k, **v} for k, v in items[:limit]]
