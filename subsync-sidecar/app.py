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

Jobs are processed one at a time by a single worker thread.
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

app = FastAPI(title="subsync-sidecar")

job_queue: "queue.Queue[str]" = queue.Queue()
jobs: dict[str, dict] = {}
jobs_lock = threading.Lock()


class SyncRequest(BaseModel):
    folder: str            # absolute, sidecar-side path
    movie_filename: str
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

    movie_path = folder / req.movie_filename
    sub_path = folder / req.subtitle_filename

    if not movie_path.is_file():
        _fail(job_id, f"Video file not found: {movie_path}")
        return
    if not sub_path.is_file():
        _fail(job_id, f"Subtitle file not found: {sub_path}")
        return

    temp_out = sub_path.with_name(sub_path.stem + "_synced_temp.srt")
    backup_path = sub_path.with_name(sub_path.stem + "_original_backup.srt")

    cmd = [
        "ffsubsync",
        str(movie_path),
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
        # Backup original, then replace it with the synced version.
        backup_path.write_bytes(sub_path.read_bytes())
        temp_out.replace(sub_path)
    except OSError as e:
        _fail(job_id, f"Post-processing failed: {e}")
        return

    with jobs_lock:
        jobs[job_id]["status"] = "done"
        jobs[job_id]["finished_at"] = time.time()
        jobs[job_id]["backup_path"] = str(backup_path)
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
