"""_run_ffsubsync: the part that touches the user's files.

Every test here runs against the fake ffsubsync from conftest, so the real
binary is never needed. The recurring assertion is `names(library)` - no path
through this function may leave a `_synced_temp` file behind, because nothing
else ever cleans them up and they accumulate in the user's library.
"""
import logging

import pytest

import app
from conftest import names

pytestmark = pytest.mark.usefixtures("fake_ffsubsync")


def test_happy_path_replaces_the_subtitle(run_sync, library):
    job = run_sync()
    assert job["status"] == "done"
    assert (library / "s.srt").read_bytes() == b"SYNCED"
    assert names(library) == ["s.srt", "v.mkv"]


def test_success_keeps_only_the_stderr_tail(run_sync):
    """The tail of stderr is where ffsubsync reports the offset it applied,
    which is the one line worth keeping. A successful run's stdout is never read
    by anything, and 8 KB per job across a library is what made this expensive.
    """
    job = run_sync()
    assert "offset seconds" in job["stderr"]
    assert "stdout" not in job


def test_failure_keeps_the_diagnostics(run_sync, library, monkeypatch, caplog):
    monkeypatch.setenv("FAKE_FFSUBSYNC_FAIL", "1")
    with caplog.at_level(logging.ERROR, logger="subsync-sidecar"):
        job = run_sync()
    assert job["status"] == "failed"
    assert "exited 3" in job["error"]
    assert "boom" in job["stderr"]
    assert "scanning audio track" in job["stdout"]
    assert (library / "s.srt").read_bytes() == b"subs"
    assert names(library) == ["s.srt", "v.mkv"]
    # The full output, not just the 2000-char slice kept on the job dict, has
    # to reach the actual log - that slice is only ever seen by someone who
    # already knows to poll GET /jobs/{id} before the job is pruned.
    assert "scanning audio track" in caplog.text
    assert "boom: could not parse subtitle" in caplog.text


def test_exit_zero_without_output_is_reported_as_an_ffsubsync_failure(run_sync, library, monkeypatch):
    """A run that returns 0 having written nothing has to be caught by the
    is_file() check, not left to blow up in the rename: the difference is
    whether the job carries ffsubsync's own diagnostics or an OSError about a
    file that was never created.
    """
    monkeypatch.setenv("FAKE_FFSUBSYNC_NO_OUTPUT", "1")
    job = run_sync()
    assert job["status"] == "failed"
    assert "ffsubsync exited 0" in job["error"]
    assert "scanning audio track" in job["stdout"]
    assert (library / "s.srt").read_bytes() == b"subs"


def test_stale_temp_file_is_removed_first(run_sync, library):
    """Debris from an earlier attempt that died before it could rename - a
    container restart, an OOM kill."""
    (library / "s_synced_temp.srt").write_bytes(b"debris from a killed container")
    job = run_sync()
    assert job["status"] == "done"
    assert (library / "s.srt").read_bytes() == b"SYNCED"
    assert names(library) == ["s.srt", "v.mkv"]


def test_stale_debris_is_never_mistaken_for_this_run_s_output(run_sync, library, monkeypatch):
    """Why the stale file is deleted up front rather than just overwritten. If
    the run then exits 0 without writing anything, a leftover temp would satisfy
    the is_file() check and get renamed over the user's subtitle - replacing it
    with the debris of a job that died days ago.
    """
    (library / "s_synced_temp.srt").write_bytes(b"debris from a killed container")
    monkeypatch.setenv("FAKE_FFSUBSYNC_NO_OUTPUT", "1")
    job = run_sync()
    assert job["status"] == "failed"
    assert (library / "s.srt").read_bytes() == b"subs"
    assert names(library) == ["s.srt", "v.mkv"]


def test_timeout_fails_the_job_and_cleans_up(run_sync, library, monkeypatch, caplog):
    monkeypatch.setenv("FAKE_FFSUBSYNC_SLEEP", "30")
    with caplog.at_level(logging.ERROR, logger="subsync-sidecar"):
        job = run_sync(timeout=1)
    assert job["status"] == "failed"
    assert "timed out after 1s" in job["error"]
    assert names(library) == ["s.srt", "v.mkv"]
    # The fake binary writes to stdout before sleeping, so subprocess.run's
    # TimeoutExpired carries that partial output - logged even though the run
    # never finished.
    assert "scanning audio track" in caplog.text


def test_missing_binary_is_a_clean_failure(run_sync, monkeypatch):
    """Almost always a broken image rather than a bad subtitle, so it's named
    explicitly instead of surfacing as a bare unhandled error from the worker."""
    monkeypatch.setenv("PATH", "/nonexistent")
    job = run_sync()
    assert job["status"] == "failed"
    assert "Could not run ffsubsync" in job["error"]


def test_missing_reference_file_fails_before_running(run_sync, library):
    job = run_sync(reference="absent.mkv")
    assert job["status"] == "failed"
    assert "Reference file not found" in job["error"]
    assert names(library) == ["s.srt", "v.mkv"]


def test_missing_subtitle_file_fails_before_running(run_sync):
    job = run_sync(subtitle="absent.srt")
    assert job["status"] == "failed"
    assert "Subtitle file not found" in job["error"]


def test_cancelled_mid_run_leaves_the_subtitle_alone(run_sync, library):
    """The load-bearing one. The plugin gave up while this ran, so it will never
    record the result; replacing the subtitle now would leave content nothing
    knows is synced, and every future sweep would sync it again - the exact loop
    the cancel endpoint exists to prevent.
    """
    job = run_sync(cancel_requested=True)
    assert job["status"] == "cancelled"
    assert (library / "s.srt").read_bytes() == b"subs"
    assert names(library) == ["s.srt", "v.mkv"]


# --- output format ----------------------------------------------------------

@pytest.mark.parametrize("subtitle", ["s.ass", "s.ssa", "s.vtt"])
def test_the_temp_file_keeps_the_subtitle_extension(run_sync, library, fake_ffsubsync, subtitle):
    """ffsubsync/pysubs2 pick the output format from the -o extension, so
    forcing .srt would silently downconvert ASS to SRT, discarding styling,
    before renaming the SRT content over the still-.ass-named original.

    Asserted on the -o argument rather than on the finished file: the rename
    puts the content at the right name either way, so the damage is only
    visible in what ffsubsync was told to produce.
    """
    (library / subtitle).write_bytes(b"styled subs")
    job = run_sync(subtitle=subtitle)
    assert job["status"] == "done"
    assert fake_ffsubsync.option("-o").endswith(subtitle.replace("s.", "_synced_temp."))
    assert (library / subtitle).read_bytes() == b"SYNCED"
    assert names(library) == sorted(["s.srt", "v.mkv", subtitle])


def test_the_command_line_is_reference_then_input_then_output(run_sync, library, fake_ffsubsync):
    run_sync()
    argv = fake_ffsubsync.argv
    assert argv[0] == str(library / "v.mkv")
    assert fake_ffsubsync.option("-i") == str(library / "s.srt")
    assert fake_ffsubsync.option("-o") == str(library / "s_synced_temp.srt")


def test_extra_args_are_appended_to_the_command(run_sync, fake_ffsubsync, monkeypatch):
    """FFSUBSYNC_EXTRA_ARGS is the documented escape hatch for flags the plugin
    knows nothing about, so it has to survive as far as the subprocess."""
    monkeypatch.setattr(app, "FFSUBSYNC_EXTRA_ARGS", ["--vad", "webrtc x"])
    run_sync()
    assert fake_ffsubsync.option("--vad") == "webrtc x"


def test_no_situation_reported_and_none_configured_leaves_ffsubsyncs_default_alone(run_sync, fake_ffsubsync):
    """Nothing is forced by default any more - a run with no embedded
    subtitle situation and no --vad in FFSUBSYNC_EXTRA_ARGS lets ffsubsync's
    own default (subs_then_webrtc) apply untouched."""
    run_sync()
    assert "--vad" not in fake_ffsubsync.argv


def test_forced_only_situation_is_applied_when_nothing_else_set_vad(run_sync, fake_ffsubsync):
    """The plugin's embedded_subtitle_situation report (a forced-only-stub
    embedded track) is translated to --vad webrtc when the user hasn't
    configured --vad themselves."""
    run_sync(embedded_subtitle_situation="forced_only")
    assert fake_ffsubsync.option("--vad") == "webrtc"


def test_users_configured_vad_wins_over_the_reported_situation(run_sync, fake_ffsubsync, monkeypatch):
    """An explicit FFSUBSYNC_EXTRA_ARGS --vad is the user's own choice and
    always takes precedence over the plugin's reported situation."""
    monkeypatch.setattr(app, "FFSUBSYNC_EXTRA_ARGS", ["--vad", "auditok"])
    run_sync(embedded_subtitle_situation="forced_only")
    assert fake_ffsubsync.argv.count("--vad") == 1
    assert fake_ffsubsync.option("--vad") == "auditok"


def test_full_or_none_situations_dont_touch_vad(run_sync, fake_ffsubsync):
    """Only "forced_only" means anything to this sidecar - "full" and "none"
    are both left to ffsubsync's own default, same as no situation at all."""
    run_sync(embedded_subtitle_situation="full")
    assert "--vad" not in fake_ffsubsync.argv


def test_full_pgs_situation_adds_the_bare_pgs_ref_stream_flag(run_sync, fake_ffsubsync):
    """No OCR involved - the plugin already established there's exactly one
    unambiguous non-forced PGS track, so the bare (auto-detect) form is
    enough. Checked by presence, not fake_ffsubsync.option(), since this
    flag takes no value and may be the last argument on the command line."""
    run_sync(embedded_subtitle_situation="full_pgs")
    assert "--pgs-ref-stream" in fake_ffsubsync.argv


def test_users_configured_reference_stream_wins_over_the_full_pgs_situation(run_sync, fake_ffsubsync, monkeypatch):
    """An explicit FFSUBSYNC_EXTRA_ARGS --reference-stream is the user's own
    choice of what to align against, and always wins - --pgs-ref-stream must
    not be added on top of it."""
    monkeypatch.setattr(app, "FFSUBSYNC_EXTRA_ARGS", ["--reference-stream", "s:1"])
    run_sync(embedded_subtitle_situation="full_pgs")
    assert "--pgs-ref-stream" not in fake_ffsubsync.argv
    assert fake_ffsubsync.option("--reference-stream") == "s:1"


def test_full_situation_with_an_index_pins_the_reference_stream(run_sync, fake_ffsubsync):
    """ffsubsync's own default pick (by longest duration) is invisible to us -
    it logs nothing about which stream it chose. When the plugin already
    resolved that ambiguity, pin it explicitly instead of trusting a choice
    we can never observe."""
    run_sync(embedded_subtitle_situation="full", embedded_subtitle_index=2)
    assert fake_ffsubsync.option("--reference-stream") == "s:2"


def test_full_situation_without_an_index_still_leaves_ffsubsync_alone(run_sync, fake_ffsubsync):
    """An older plugin that predates this field reports "full" with no index -
    falls back to the prior behavior rather than guessing one."""
    run_sync(embedded_subtitle_situation="full", embedded_subtitle_index=None)
    assert "--reference-stream" not in fake_ffsubsync.argv


def test_users_configured_reference_stream_wins_over_the_full_situation(run_sync, fake_ffsubsync, monkeypatch):
    """An explicit FFSUBSYNC_EXTRA_ARGS --reference-stream is the user's own
    choice and always wins, even when the plugin also reported an index."""
    monkeypatch.setattr(app, "FFSUBSYNC_EXTRA_ARGS", ["--reference-stream", "s:3"])
    run_sync(embedded_subtitle_situation="full", embedded_subtitle_index=2)
    assert fake_ffsubsync.argv.count("--reference-stream") == 1
    assert fake_ffsubsync.option("--reference-stream") == "s:3"


def test_full_pgs_situation_with_an_index_pins_the_pgs_reference_stream(run_sync, fake_ffsubsync):
    """The plugin's index is more precise than the bare auto-detect form, so
    use it when given."""
    run_sync(embedded_subtitle_situation="full_pgs", embedded_subtitle_index=4)
    assert fake_ffsubsync.option("--pgs-ref-stream") == "s:4"


# --- alignment metrics and the score gate ------------------------------------

def test_done_job_carries_the_parsed_metrics(run_sync):
    job = run_sync()
    assert job["status"] == "done"
    assert job["score"] == 33134.0
    assert job["offset_seconds"] == 1.5
    assert job["framerate_scale_factor"] == 1.0
    assert job["low_quality"] is False


def test_negative_score_fails_the_job_and_leaves_the_subtitle_alone(run_sync, library, monkeypatch, caplog):
    """ffsubsync exits 0 even when its best alignment is anti-correlated, so the
    score line is the only thing standing between a bad run and an overwritten
    subtitle."""
    monkeypatch.setenv("FAKE_FFSUBSYNC_NEGATIVE_SCORE", "1")
    with caplog.at_level(logging.ERROR, logger="subsync-sidecar"):
        job = run_sync()
    assert job["status"] == "failed"
    assert "alignment rejected" in job["error"]
    assert job["score"] == -72067.32
    assert (library / "s.srt").read_bytes() == b"subs"
    assert names(library) == ["s.srt", "v.mkv"]
    # This is the exact case a one-line "alignment rejected (score ...)"
    # summary is useless for - the full output is what would actually explain
    # why the score came out anti-correlated.
    assert "score: -72067.320" in caplog.text
    assert "scanning audio track" in caplog.text


def test_low_quality_refusal_is_reported_as_failed(run_sync, library, monkeypatch, caplog):
    """With --skip-sync-on-low-quality ffsubsync writes the original back and
    exits 0. Reporting that as done would let the skip-cache pin an unsynced
    file forever; failed lets the fail-cache retry and then give up."""
    monkeypatch.setenv("FAKE_FFSUBSYNC_LOW_QUALITY", "1")
    with caplog.at_level(logging.ERROR, logger="subsync-sidecar"):
        job = run_sync()
    assert job["status"] == "failed"
    assert job["low_quality"] is True
    assert (library / "s.srt").read_bytes() == b"subs"
    assert names(library) == ["s.srt", "v.mkv"]
    assert "low-quality alignment" in caplog.text


# --- KEEP_ORIGINAL_SUBTITLE_BACKUP -----------------------------------------

def test_no_backup_is_kept_by_default(run_sync, library):
    job = run_sync()
    assert job["backup_path"] is None
    assert names(library) == ["s.srt", "v.mkv"]


def test_backup_keeps_the_pre_sync_subtitle(run_sync, library, monkeypatch):
    monkeypatch.setattr(app, "KEEP_ORIGINAL_SUBTITLE_BACKUP", True)
    job = run_sync()
    assert job["status"] == "done"
    backup = library / "s_original_backup.srt"
    assert backup.read_bytes() == b"subs"
    assert (library / "s.srt").read_bytes() == b"SYNCED"
    assert job["backup_path"] == str(backup)


def test_a_cancelled_job_writes_no_backup(run_sync, library, monkeypatch):
    monkeypatch.setattr(app, "KEEP_ORIGINAL_SUBTITLE_BACKUP", True)
    run_sync(cancel_requested=True)
    assert names(library) == ["s.srt", "v.mkv"]
