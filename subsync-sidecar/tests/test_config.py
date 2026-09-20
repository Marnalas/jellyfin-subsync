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


# --- FFSUBSYNC_EXTRA_ARGS: nothing is injected -------------------------------

def test_extra_args_env_is_used_as_is_with_nothing_added(reloaded_app):
    """The sidecar no longer appends anything of its own - FFSUBSYNC_EXTRA_ARGS
    is exactly what the user configured, and any --vad override implied by
    the plugin's reported jellyfin_reported_situation is applied later, per
    request, in _run_ffsubsync rather than baked into this constant."""
    reloaded = reloaded_app(FFSUBSYNC_EXTRA_ARGS="--max-duration-seconds 1200")
    assert reloaded.FFSUBSYNC_EXTRA_ARGS == ["--max-duration-seconds", "1200"]


def test_extra_args_env_unset_is_empty(reloaded_app):
    reloaded = reloaded_app(FFSUBSYNC_EXTRA_ARGS="")
    assert reloaded.FFSUBSYNC_EXTRA_ARGS == []


# --- _parse_ffsubsync_result -------------------------------------------------

def test_parse_reads_the_last_reported_values():
    stderr = (
        "INFO score: 12.000\nINFO offset seconds: 3.100\n"
        "INFO score: -1398.000\nINFO offset seconds: 0.570\n"
        "INFO framerate scale factor: 1.043\n"
    )
    assert app._parse_ffsubsync_result(stderr) == {
        "score": -1398.0,
        "offset_seconds": 0.57,
        "framerate_scale_factor": 1.043,
        "low_quality": False,
    }


def test_parse_tolerates_missing_lines():
    assert app._parse_ffsubsync_result("nothing useful") == {
        "score": None, "offset_seconds": None, "framerate_scale_factor": None, "low_quality": False,
    }


# --- SyncRequest: pre-rename field names still work --------------------------

_SYNC_REQUEST_BASE = {"folder": "/f", "reference_filename": "v.mkv", "subtitle_filename": "s.srt"}


def test_sync_request_accepts_the_pre_rename_field_names():
    """A plugin that hasn't been upgraded past the embedded_subtitle_situation/
    embedded_subtitle_index -> jellyfin_reported_situation/reference_stream_index
    rename must not lose its forced_only/full/full_pgs handling just because
    the sidecar was upgraded first - same information, only the JSON keys
    changed."""
    req = app.SyncRequest(
        **_SYNC_REQUEST_BASE, embedded_subtitle_situation="forced_only", embedded_subtitle_index=2)
    assert req.jellyfin_reported_situation == "forced_only"
    assert req.reference_stream_index == 2


def test_sync_request_still_accepts_the_current_field_names():
    req = app.SyncRequest(
        **_SYNC_REQUEST_BASE, jellyfin_reported_situation="full", reference_stream_index=1)
    assert req.jellyfin_reported_situation == "full"
    assert req.reference_stream_index == 1


def test_sync_request_with_neither_name_defaults_to_none():
    req = app.SyncRequest(**_SYNC_REQUEST_BASE)
    assert req.jellyfin_reported_situation is None
    assert req.reference_stream_index is None


# --- _reference_args_for ------------------------------------------------------

def test_retry_on_fail_situation_maps_to_webrtc_and_audio_reference_stream():
    """The opt-in retry report forces the same --vad webrtc fallback as
    forced_only, plus an audio-stream --reference-stream pin - "a:", not
    "s:" like every other situation's index."""
    assert app._reference_args_for("attempt_on_failed", 1, []) == ["--vad", "webrtc", "--reference-stream", "a:1"]


def test_retry_on_fail_situation_without_an_index_only_forces_webrtc():
    """No audio stream to point at doesn't stop the VAD fallback - the two
    additions are independent."""
    assert app._reference_args_for("attempt_on_failed", None, []) == ["--vad", "webrtc"]


def test_retry_on_fail_situation_skips_vad_when_the_user_already_set_it():
    """The two flags are independently overridable - the user's own --vad
    wins, but the audio-stream pin is still added."""
    assert app._reference_args_for("attempt_on_failed", 1, ["--vad", "auditok"]) == ["--reference-stream", "a:1"]


@pytest.mark.parametrize("blocking_arg", ["--reference-stream", "--refstream", "--reference-track", "--reftrack"])
def test_retry_on_fail_situation_skips_reference_stream_when_the_user_already_chose_one(blocking_arg):
    """Same independence the other way: the user's own --reference-stream
    (or alias) wins, but --vad webrtc is still forced."""
    assert app._reference_args_for("attempt_on_failed", 1, [blocking_arg, "a:7"]) == ["--vad", "webrtc"]


def test_forced_only_situation_maps_to_webrtc():
    """The one text situation ffsubsync's own subs_then_webrtc default gets
    wrong: nothing to lock onto if the only embedded track(s) are forced-only
    stubs."""
    assert app._reference_args_for("forced_only", None, []) == ["--vad", "webrtc"]


def test_forced_only_situation_is_skipped_when_the_user_already_set_vad():
    """The user's own --vad choice is their explicit intent and always wins."""
    assert app._reference_args_for("forced_only", None, ["--vad", "auditok"]) == []


def test_full_pgs_situation_maps_to_pgs_ref_stream():
    """No full text track exists, but the plugin found exactly one
    unambiguous non-forced PGS track - ffsubsync can align against it via
    packet-display timing, no OCR involved. Bare form auto-detects it when
    the plugin didn't also report the stream's index."""
    assert app._reference_args_for("full_pgs", None, []) == ["--pgs-ref-stream"]


def test_full_pgs_situation_with_an_index_pins_the_pgs_reference_stream():
    """More precise than the bare auto-detect form, so used when given."""
    assert app._reference_args_for("full_pgs", 4, []) == ["--pgs-ref-stream", "s:4"]


@pytest.mark.parametrize("blocking_arg", [
    "--vad", "--pgs-ref-stream", "--pgsstream",
    "--reference-stream", "--refstream", "--reference-track", "--reftrack",
])
def test_full_pgs_situation_is_skipped_when_the_user_already_chose_a_reference(blocking_arg):
    """Any of --vad, --pgs-ref-stream (or its alias), or --reference-stream
    (or any of its aliases) is the user's own explicit choice and always
    wins over the plugin's report."""
    assert app._reference_args_for("full_pgs", 4, [blocking_arg, "something"]) == []


def test_full_situation_without_an_index_adds_nothing():
    """ffsubsync's own default already handles a good text track correctly on
    its own when the plugin has no index to hand it (an older plugin that
    predates this field)."""
    assert app._reference_args_for("full", None, []) == []


def test_full_situation_with_an_index_pins_the_reference_stream():
    """ffsubsync's own stream pick here is invisible to us - it logs nothing
    about which one it chose. Pin it explicitly when the plugin resolved
    that ambiguity itself."""
    assert app._reference_args_for("full", 2, []) == ["--reference-stream", "s:2"]


@pytest.mark.parametrize("blocking_arg", [
    "--reference-stream", "--refstream", "--reference-track", "--reftrack",
])
def test_full_situation_with_an_index_is_skipped_when_the_user_already_chose_a_reference(blocking_arg):
    """The user's own --reference-stream (or alias) is their explicit choice
    and always wins over the plugin's reported index."""
    assert app._reference_args_for("full", 2, [blocking_arg, "something"]) == []


@pytest.mark.parametrize("situation", ["none", None, "some_future_value_this_sidecar_predates"])
def test_every_other_situation_adds_nothing(situation):
    """No embedded track at all, and an unrecognized value - a newer plugin
    talking to an older sidecar - are both treated the same as absent rather
    than raising, matching this file's general posture on unexpected input."""
    assert app._reference_args_for(situation, None, []) == []
    assert app._reference_args_for(situation, 2, []) == []
