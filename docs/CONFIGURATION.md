# Configuration

## Timeouts and job budgets

Two independent budgets, because they measure different things:

| Budget | Setting | Default | Enforced by | Covers |
| --- | --- | --- | --- | --- |
| Run | **Job timeout** | 1800s | the sidecar | time a job spends actually running ffsubsync |
| Queue wait | **Queue wait timeout** | 3600s | the plugin | time a job spends waiting for a free worker (0 = wait forever) |

The plugin sends its run budget with every job, and the sidecar is the side
that enforces it - the plugin deliberately waits a little longer than the
number it sent, so the sidecar is always the one to declare a timeout. That
ordering is what stops a job from being abandoned while it's still running and
then overwriting the subtitle afterwards, which used to leave a file that got
re-synced on every subsequent sweep.

Queue time is not charged against the run budget. If jobs are queuing for
longer than an hour, the plugin's **Max parallel jobs** is likely set well above
the sidecar's `MAX_PARALLEL_JOBS`; the log message names both. When the plugin
does give up, it tells the sidecar to drop the job, so the result is discarded
rather than written over the subtitle.

**Version skew:** with a sidecar older than 3.0.0.0, the run budget you set here
isn't sent, and that sidecar applies its own hardcoded 30 minutes instead. Jobs
still fail cleanly - the setting simply won't take effect until the sidecar is
updated.

## Extra ffsubsync flags (`FFSUBSYNC_EXTRA_ARGS`)

The sidecar's `FFSUBSYNC_EXTRA_ARGS` environment variable (set in the
sidecar's own `compose.yml`, not on the plugin) is appended verbatim to every
`ffsubsync` invocation, shell-quoting rules applied. Neither the plugin nor
the sidecar validates its contents, so anything `ffsubsync --help` accepts is
fair game - but a bad flag fails that sync job rather than being caught
earlier, and with **Max consecutive failures** set (Dashboard > Plugins >
Subsync), a flag that's wrong for a whole library will make every file in it
stop being retried after a few sweeps.

A few flags that make sense in this plugin's context:

- **`--split-penalty` - subtitles that drift partway through the file.** By
  default ffsubsync fits one global offset for the whole file. `--split-penalty`
  switches to alass-style piecewise sync, letting the offset change partway
  through to correct for commercial breaks, inserted/removed scenes (director's
  vs. theatrical cuts), or two discs concatenated into one file - exactly the
  case where a subtitle syncs fine at the start and drifts later. Pass it bare
  for a reasonable default cost, or a number (typically 4-20; lower splits more
  eagerly) to tune it:
  ```yaml
  FFSUBSYNC_EXTRA_ARGS: "--split-penalty"
  FFSUBSYNC_EXTRA_ARGS: "--split-penalty 8"
  ```
- **`--reference-stream` - align to a specific audio/subtitle track.** ffsubsync
  otherwise picks the first audio stream in the video as its reference. On a
  release where that's a commentary track or a dub in another language, point
  it at the right one explicitly (ffmpeg stream-specifier syntax, leading `0:`
  optional): `--reference-stream a:1` for the second audio track, or
  `--reference-stream s:0` to align against an existing (correctly-timed)
  embedded subtitle track instead of audio at all.
- **`--max-offset-seconds` (default 60) - widen or narrow the search window.**
  Raise it if a subtitle is known to be off by more than a minute (subtitle
  pulled from a different regional cut with a longer intro, for example);
  lower it to fail fast instead of risking an alignment to a coincidental
  match far from where the real one is.
- **`--vad` - swap the voice-activity detector.** The sidecar itself never
  adds this - it runs whatever `FFSUBSYNC_EXTRA_ARGS` says, nothing more.
  ffsubsync's own default is `subs_then_webrtc`: when the video carries an
  embedded text subtitle stream it aligns against that stream instead of the
  audio, which is usually the *better* choice (no audio extraction, no VAD
  false-positives on noisy mixes or sparse dialogue). The plugin only steps
  in for the one case that default gets wrong: when Jellyfin reports that an
  item's embedded subtitle stream(s) are **all forced** (signs/foreign lines,
  a few dozen cues - a forced-only stub with nothing for subs-based alignment
  to lock onto). ffsubsync would otherwise shift a good subtitle by up to
  `--max-offset-seconds` with a negative score
  ([smacke/ffsubsync#238](https://github.com/smacke/ffsubsync/issues/238)).
  In that specific situation, and only when the subtitle being synced has no
  already-synced sibling to align against instead (i.e. the sidecar would be
  aligning against the video itself), the plugin asks the sidecar to use
  `--vad webrtc` for that one job. Every other case - no embedded subtitle at
  all, or at least one full/non-forced embedded track - leaves ffsubsync's
  own default alone. An explicit `--vad ...` in `FFSUBSYNC_EXTRA_ARGS` always
  wins over the plugin's per-job request, so you can override the backend
  globally regardless: `--vad auditok` is a CPU-only alternative if webrtc
  struggles on a particular library, and `--vad subs_then_webrtc` pins
  ffsubsync's own default even for a forced-only-stub item, if you'd rather
  have that. The `silero` and `fused*` backends need the optional `torch`
  dependency, which the published sidecar image does not install (no GPU base
  image, per the sidecar's Dockerfile) - they'll fail on an unmodified image.

  **Version skew:** the per-job `--vad` request needs both a plugin and a
  sidecar new enough to speak it - an older sidecar ignores the field
  entirely (ffsubsync's own default applies, forced-only stubs and all), and
  an older plugin never sends it (same result). Nothing breaks either way,
  the forced-only-stub handling just doesn't kick in until both sides are
  upgraded.
- **`--skip-sync-on-low-quality` - refuse a low-confidence alignment.** Not
  added automatically - add it yourself if you want it. It makes ffsubsync
  leave the subtitle's timing unchanged instead of applying an alignment it
  isn't confident about (tune the threshold with `--min-score`,
  `--quality-max-offset-seconds`, `--max-framerate-deviation`). ffsubsync
  still exits 0 when it triggers, so the sidecar reads its log for the marker
  string it prints (`leaving subtitles unmodified`) and reports the job as
  **failed** with the original file untouched, rather than letting an
  unconfident no-op look like a successful sync. The plugin's fail-cache then
  retries it a few more sweeps and stops.

  Independently of this flag, and always on: the sidecar also parses
  ffsubsync's reported `score:` from its log on every run, and rejects the
  result the same way - **failed**, file untouched - whenever that score is
  negative. ffsubsync exits 0 on an anti-correlated alignment just as
  happily as on a good one, so this is what stops a silently bad shift from
  ever being written, whether or not `--skip-sync-on-low-quality` is set. The
  parsed `score`, `offset_seconds` and `framerate_scale_factor` are on every
  finished job (`GET /jobs/{id}`), for done and failed alike.
- **`--suppress-output-if-offset-less-than` - the opposite caveat.** When the
  computed offset is below the threshold, ffsubsync writes *no* output file at
  all. The sidecar's success check requires that file to exist, so this reads
  as a failed job - combined with the fail-cache, a subtitle that's already
  close enough will accumulate failures and eventually stop being retried,
  which is a harmless end state but shows up as "failed" rather than "skipped"
  in the sync history.
