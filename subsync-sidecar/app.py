"""
subsync-sidecar
================
A minimal, always-on HTTP service that wraps ffsubsync. Runs as a persistent
docker-compose, so the Jellyfin plugin can trigger syncs over the network
without touching the docker socket or the Jellyfin container.

Endpoints:
  GET  /health              -> {"status": "ok"}
  POST /sync                -> queue a sync job, returns {"job_id": "..."}
  GET  /jobs/{job_id}       -> job status: queued | running | done | failed
  GET  /jobs                -> list recent jobs

Jobs are processed by a pool of MAX_PARALLEL_JOBS worker threads.
"""
import os
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

# no --vad flag, so ffsubsync uses its default (webrtc). Switching to
# GPU-accelerated silero VAD is a later, separate step once this known-working
# baseline is confirmed running.
FFSUBSYNC_EXTRA_ARGS = os.environ.get("FFSUBSYNC_EXTRA_ARGS", "").split()

# ffsubsync only decodes the audio track (via ffmpeg), not the full video, so
# it's light enough per-job to run several at once on a multi-core host.
# Leave one core free by default for the rest of the system (Jellyfin
# transcoding, etc, often shares the same box); override explicitly via
# MAX_PARALLEL_JOBS if that guess is wrong for your setup (e.g. the
# container has a `--cpus` limit lower than the host's core count).
# An unset, "0", or unparseable value all mean "auto-detect", as documented in
# compose.yml - taking int("0") at face value would start zero worker threads,
# leaving every submitted job queued forever with nothing to run it.
_max_parallel_env = os.environ.get("MAX_PARALLEL_JOBS", "").strip()
try:
    _configured_parallel_jobs = int(_max_parallel_env) if _max_parallel_env else 0
except ValueError:
    log.warning("Ignoring unparseable MAX_PARALLEL_JOBS=%r, auto-detecting instead", _max_parallel_env)
    _configured_parallel_jobs = 0
MAX_PARALLEL_JOBS = _configured_parallel_jobs if _configured_parallel_jobs > 0 else max(1, (os.cpu_count() or 1) - 1)

# Off by default: the synced subtitle replaces the original in place and no
# copy is kept. Set to "true" to keep a "<name>_original_backup<ext>" copy
# of the pre-sync subtitle alongside it.
KEEP_ORIGINAL_SUBTITLE_BACKUP = os.environ.get("KEEP_ORIGINAL_SUBTITLE_BACKUP", "").strip().lower() in ("1", "true", "yes")

app = FastAPI(title="subsync-sidecar")

job_queue: "queue.Queue[str]" = queue.Queue()
jobs: dict[str, dict] = {}
jobs_lock = threading.Lock()


class SyncRequest(BaseModel):
    folder: str            # absolute, sidecar-side path
    reference_filename: str
    subtitle_filename: str


def _run_ffsubsync(job_id: str, req: SyncRequest):
    with jobs_lock:
        jobs[job_id]["status"] = "running"
        jobs[job_id]["started_at"] = time.time()

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

    cmd = [
        "ffsubsync",
        str(reference_path),
        "-i", str(sub_path),
        "-o", str(temp_out),
        *FFSUBSYNC_EXTRA_ARGS,
    ]

    log.info("Job %s: running %s", job_id, " ".join(cmd))
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=1800)
    except subprocess.TimeoutExpired:
        _fail(job_id, "ffsubsync timed out after 30 minutes")
        return

    with jobs_lock:
        jobs[job_id]["stdout"] = result.stdout[-4000:]
        jobs[job_id]["stderr"] = result.stderr[-4000:]

    if result.returncode != 0 or not temp_out.is_file():
        _fail(job_id, f"ffsubsync exited {result.returncode}")
        return

    try:
        if backup_path is not None:
            # Backup original, then replace it with the synced version.
            backup_path.write_bytes(sub_path.read_bytes())
        temp_out.replace(sub_path)
    except OSError as e:
        _fail(job_id, f"Post-processing failed: {e}")
        return

    with jobs_lock:
        jobs[job_id]["status"] = "done"
        jobs[job_id]["finished_at"] = time.time()
        jobs[job_id]["backup_path"] = str(backup_path) if backup_path is not None else None
    log.info("Job %s: done", job_id)


def _fail(job_id: str, message: str):
    with jobs_lock:
        jobs[job_id]["status"] = "failed"
        jobs[job_id]["error"] = message
        jobs[job_id]["finished_at"] = time.time()
    log.error("Job %s: %s", job_id, message)


def _worker():
    while True:
        job_id = job_queue.get()
        req = jobs[job_id]["request"]
        try:
            _run_ffsubsync(job_id, SyncRequest(**req))
        except Exception as e:  # keep the worker alive no matter what
            _fail(job_id, f"Unhandled error: {e}")
        job_queue.task_done()


log.info("Starting %d sync worker thread(s) (MAX_PARALLEL_JOBS)", MAX_PARALLEL_JOBS)
for _ in range(MAX_PARALLEL_JOBS):
    threading.Thread(target=_worker, daemon=True).start()


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/sync")
def create_sync(req: SyncRequest):
    job_id = str(uuid.uuid4())
    with jobs_lock:
        jobs[job_id] = {
            "status": "queued",
            "request": req.model_dump(),
            "queued_at": time.time(),
        }
    job_queue.put(job_id)
    return {"job_id": job_id}


@app.get("/jobs/{job_id}")
def get_job(job_id: str):
    with jobs_lock:
        job = jobs.get(job_id)
    if not job:
        raise HTTPException(status_code=404, detail="job not found")
    return {"job_id": job_id, **job}


@app.get("/jobs")
def list_jobs(limit: int = 50):
    with jobs_lock:
        items = sorted(jobs.items(), key=lambda kv: kv[1].get("queued_at", 0), reverse=True)
    return [{"job_id": k, **v} for k, v in items[:limit]]
