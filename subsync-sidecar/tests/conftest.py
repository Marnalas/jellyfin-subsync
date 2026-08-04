"""Shared fixtures for the sidecar suite.

The module under test keeps its state in globals (`jobs`, `job_queue`), so the
autouse `clean_state` fixture below is what makes the suite order-independent.
Worker threads are *not* running by default: they're started by the app
lifespan, so a plain TestClient gives an app with nothing draining the queue,
which is what lets a test assert a job is still "queued". The one test that
wants a real worker asks for `worker_client`.
"""
import os
import sys
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

# pytest.ini sets `pythonpath = .`, which covers the documented invocation
# (`pytest` from subsync-sidecar/, and the CI job). This covers the rest -
# `pytest subsync-sidecar` from the repo root, or an IDE with its own rootdir -
# where that ini is never loaded and `import app` would fail with nothing to
# suggest why.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import app  # noqa: E402

# Stand-in for the real ffsubsync. Parses just enough of the command line to
# find -o, and writes a marker there. Behaviour is driven by env vars so a test
# can ask for a failure, a slow run, or a run that exits 0 without producing
# anything - the three cases that separate app.py's error branches.
FAKE_FFSUBSYNC = """#!/bin/sh
# Record the command line before parsing consumes it, so tests can assert on
# what app.py actually asked ffsubsync to do rather than only on the result.
if [ -n "$FAKE_FFSUBSYNC_ARGV" ]; then
  : > "$FAKE_FFSUBSYNC_ARGV"
  for arg in "$@"; do printf '%s\\n' "$arg" >> "$FAKE_FFSUBSYNC_ARGV"; done
fi

out=""
while [ $# -gt 0 ]; do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    *) shift ;;
  esac
done

if [ -n "$FAKE_FFSUBSYNC_SLEEP" ]; then sleep "$FAKE_FFSUBSYNC_SLEEP"; fi

# stdout: app.py keeps this only on failure, so its absence is assertable.
echo "scanning audio track"

if [ "$FAKE_FFSUBSYNC_FAIL" = "1" ]; then
  echo "boom: could not parse subtitle" >&2
  exit 3
fi

# stderr: the real ffsubsync reports the applied offset here, and app.py keeps
# the tail of it on success for exactly that reason.
echo "offset seconds: 1.5" >&2

if [ "$FAKE_FFSUBSYNC_NO_OUTPUT" != "1" ]; then printf 'SYNCED' > "$out"; fi
"""


@pytest.fixture(autouse=True)
def clean_state():
    """Reset the module globals around every test."""
    _drain()
    app.jobs.clear()
    yield
    app.stop_workers(timeout=2.0)
    _drain()
    app.jobs.clear()


def _drain():
    while not app.job_queue.empty():
        app.job_queue.get_nowait()
        app.job_queue.task_done()


@pytest.fixture
def client():
    """A client with no worker pool: the lifespan never runs, so queued jobs
    stay queued and the HTTP layer can be tested on its own."""
    return TestClient(app.app)


@pytest.fixture
def worker_client(monkeypatch):
    """A client with the real worker pool running, via the lifespan. Used for
    the end-to-end test only; `clean_state` stops the pool afterwards."""
    monkeypatch.setattr(app, "MAX_PARALLEL_JOBS", 1)
    with TestClient(app.app) as running_client:
        yield running_client


class FakeFfsubsync:
    """Handle on the stand-in binary: mainly a way to read back the command line
    it was invoked with."""

    def __init__(self, script, argv_record):
        self.script = script
        self._argv_record = argv_record

    @property
    def argv(self):
        if not self._argv_record.exists():
            return []
        return self._argv_record.read_text().splitlines()

    def option(self, flag):
        """The value passed after `flag`, or None if it wasn't passed."""
        argv = self.argv
        return argv[argv.index(flag) + 1] if flag in argv else None


@pytest.fixture
def fake_ffsubsync(tmp_path, monkeypatch):
    """Put a controllable `ffsubsync` at the front of PATH."""
    bindir = tmp_path / "bin"
    bindir.mkdir()
    script = bindir / "ffsubsync"
    script.write_text(FAKE_FFSUBSYNC)
    script.chmod(0o755)
    argv_record = bindir / "argv.txt"
    monkeypatch.setenv("PATH", f"{bindir}{os.pathsep}{os.environ['PATH']}")
    monkeypatch.setenv("FAKE_FFSUBSYNC_ARGV", str(argv_record))
    return FakeFfsubsync(script, argv_record)


@pytest.fixture
def library(tmp_path):
    """A media folder shaped the way the plugin sends them: one reference video
    and one external subtitle beside it."""
    folder = tmp_path / "library"
    folder.mkdir()
    (folder / "v.mkv").write_bytes(b"video")
    (folder / "s.srt").write_bytes(b"subs")
    return folder


@pytest.fixture
def make_job():
    """Register a job in the state a worker would have left it in, and hand back
    its id. Bypasses the queue so `_run_ffsubsync` can be driven directly."""
    counter = iter(range(1, 1000))

    def _make(status="running", **fields):
        job_id = f"job-{next(counter)}"
        now = time.time()
        app.jobs[job_id] = {
            "status": status,
            "request": {},
            "queued_at": now,
            "started_at": now,
            "timeout_seconds": 10,
            "cancel_requested": False,
            **fields,
        }
        return job_id

    return _make


@pytest.fixture
def run_sync(library, make_job):
    """Run `_run_ffsubsync` end to end against `library` and return the job."""
    def _run(folder=None, reference="v.mkv", subtitle="s.srt", timeout=10, **fields):
        job_id = make_job(timeout_seconds=timeout, **fields)
        request = app.SyncRequest(
            folder=str(library if folder is None else folder),
            reference_filename=reference,
            subtitle_filename=subtitle,
            timeout_seconds=timeout,
        )
        app._run_ffsubsync(job_id, request, timeout)
        return app.jobs[job_id]

    return _run


def names(folder):
    """Sorted filenames in a folder - used to assert no temp file was left."""
    return sorted(p.name for p in folder.iterdir())
