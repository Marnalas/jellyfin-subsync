"""Environment parsing and the timeout budget.

The guiding rule in app.py is that a typo in a compose file degrades to the
default rather than stopping the sidecar from starting, so most of these are
about malformed input.
"""
import importlib
import logging
import os

import pytest

import app


@pytest.fixture
def reloaded_app():
    """Re-import app.py under a different environment.

    Only safe because importing the module no longer spawns worker threads -
    the constants under test here are all computed at module scope, so there is
    no other way to reach them. Environment, cpu_count and the module itself are
    all restored afterwards.
    """
    saved_env = dict(os.environ)
    saved_cpu_count = os.cpu_count

    def _reload(cpu_count=None, **env):
        os.environ.update({k: str(v) for k, v in env.items()})
        if cpu_count is not None:
            os.cpu_count = lambda: cpu_count
        return importlib.reload(app)

    yield _reload

    os.cpu_count = saved_cpu_count
    os.environ.clear()
    os.environ.update(saved_env)
    importlib.reload(app)


# --- _env_int ---------------------------------------------------------------

def test_env_int_unset_uses_the_default(monkeypatch):
    monkeypatch.delenv("SUBSYNC_TEST_INT", raising=False)
    assert app._env_int("SUBSYNC_TEST_INT", 42) == 42


@pytest.mark.parametrize("raw", ["", "   ", "\t"])
def test_env_int_blank_uses_the_default(monkeypatch, raw):
    """compose.yml ships several of these knobs as `KEY: ""`, so blank is the
    documented way to say "leave it alone", not a mistake."""
    monkeypatch.setenv("SUBSYNC_TEST_INT", raw)
    assert app._env_int("SUBSYNC_TEST_INT", 42) == 42


def test_env_int_reads_a_valid_value(monkeypatch):
    monkeypatch.setenv("SUBSYNC_TEST_INT", "7")
    assert app._env_int("SUBSYNC_TEST_INT", 42) == 7


def test_env_int_unparseable_warns_and_falls_back(monkeypatch, caplog):
    monkeypatch.setenv("SUBSYNC_TEST_INT", "1800s")
    with caplog.at_level(logging.WARNING):
        assert app._env_int("SUBSYNC_TEST_INT", 42) == 42
    assert "1800s" in caplog.text


def test_env_int_below_minimum_warns_and_falls_back(monkeypatch, caplog):
    monkeypatch.setenv("SUBSYNC_TEST_INT", "5")
    with caplog.at_level(logging.WARNING):
        assert app._env_int("SUBSYNC_TEST_INT", 3600, minimum=60) == 3600
    assert "minimum" in caplog.text


def test_env_int_accepts_zero_when_the_minimum_allows_it(monkeypatch):
    """MAX_PARALLEL_JOBS relies on this: 0 has to survive parsing so the
    auto-detect branch can see it."""
    monkeypatch.setenv("SUBSYNC_TEST_INT", "0")
    assert app._env_int("SUBSYNC_TEST_INT", 4, minimum=0) == 0


# --- _env_args --------------------------------------------------------------

def test_env_args_unset_is_empty(monkeypatch):
    monkeypatch.delenv("SUBSYNC_TEST_ARGS", raising=False)
    assert app._env_args("SUBSYNC_TEST_ARGS") == []


def test_env_args_keeps_a_quoted_argument_in_one_piece(monkeypatch):
    """The regression this helper exists for: a bare str.split() turned
    --vad "webrtc x" into three tokens carrying literal quote characters, and
    the job failed on an argument the user could see was correct."""
    monkeypatch.setenv("SUBSYNC_TEST_ARGS", '--vad "webrtc x" --max-offset 60')
    assert app._env_args("SUBSYNC_TEST_ARGS") == ["--vad", "webrtc x", "--max-offset", "60"]


def test_env_args_unbalanced_quote_warns_and_falls_back(monkeypatch, caplog):
    monkeypatch.setenv("SUBSYNC_TEST_ARGS", '--vad "webrtc')
    with caplog.at_level(logging.WARNING):
        assert app._env_args("SUBSYNC_TEST_ARGS") == []
    assert "SUBSYNC_TEST_ARGS" in caplog.text


# --- MAX_PARALLEL_JOBS ------------------------------------------------------

def test_explicit_parallel_jobs_is_respected(reloaded_app):
    assert reloaded_app(MAX_PARALLEL_JOBS=3).MAX_PARALLEL_JOBS == 3


@pytest.mark.parametrize("raw", ["0", "", "banana"])
def test_parallel_jobs_auto_detects_leaving_one_core_free(reloaded_app, raw):
    assert reloaded_app(cpu_count=8, MAX_PARALLEL_JOBS=raw).MAX_PARALLEL_JOBS == 7


def test_parallel_jobs_never_auto_detects_to_zero(reloaded_app):
    """cpu_count - 1 is 0 on a single-core host. Starting zero worker threads
    would leave every submitted job queued forever with nothing to run it."""
    assert reloaded_app(cpu_count=1, MAX_PARALLEL_JOBS="0").MAX_PARALLEL_JOBS == 1


def test_retention_floor_is_enforced(reloaded_app):
    """A TTL near the plugin's poll interval would evict jobs before they were
    read, and the plugin reads a 404 as a failed sync."""
    assert reloaded_app(JOB_RETENTION_SECONDS=5).JOB_RETENTION_SECONDS == 3600


@pytest.mark.parametrize("raw,expected", [("true", True), ("TRUE", True), ("1", True),
                                          ("yes", True), (" true ", True),
                                          ("false", False), ("", False), ("no", False)])
def test_backup_flag_parsing(reloaded_app, raw, expected):
    assert reloaded_app(KEEP_ORIGINAL_SUBTITLE_BACKUP=raw).KEEP_ORIGINAL_SUBTITLE_BACKUP is expected


# --- _effective_timeout -----------------------------------------------------

@pytest.mark.parametrize("requested", [None, 0, -1])
def test_absent_or_nonsense_timeout_uses_the_default(requested):
    """None is what a plugin older than 3.0.0.0 sends - it had no say in the
    budget at all."""
    assert app._effective_timeout(requested) == app.JOB_TIMEOUT_SECONDS


def test_timeout_under_the_ceiling_passes_through():
    assert app._effective_timeout(600) == 600


def test_timeout_over_the_ceiling_is_clamped():
    """A mistyped plugin setting must not pin a worker thread for a day."""
    assert app._effective_timeout(999_999) == app.MAX_JOB_TIMEOUT_SECONDS
