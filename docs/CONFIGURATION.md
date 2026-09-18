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
  adds this on its own - it runs whatever `FFSUBSYNC_EXTRA_ARGS` says, nothing
  more, and has no opinion of its own about which VAD backend to use.
  ffsubsync's own default is `subs_then_webrtc`: when the video carries an
  embedded **text** subtitle stream (`.srt`/`.ass`/`.ssa`/etc, not an image
  format - see `--pgs-ref-stream` below for those) it aligns against
  whichever such stream's cues run latest into the file, instead of the
  audio - usually the *better* choice (no audio extraction, no VAD
  false-positives on noisy mixes or sparse dialogue). What the sidecar *does*
  do is act on a fact the plugin reports with every sync job: what Jellyfin
  knows about the video's own embedded **text** subtitle stream(s) - none, a
  full track, or **all forced** (signs/foreign lines, a few dozen cues - a
  forced-only stub with nothing for subs-based alignment to lock onto).
  Image-based subtitle codecs (PGS, VobSub, DVB) are never counted as a
  "full track" here, even when non-forced, because ffsubsync's own default
  comparison never considers them either - see `--pgs-ref-stream` below for
  what does happen with those. The plugin only ever reports this when the
  subtitle being synced has no already-synced sibling to align against
  instead (i.e. the sidecar would be aligning against the video itself) -
  otherwise a placeholder "irrelevant" value is sent, since VAD has no effect
  when the reference is another subtitle file. When the sidecar sees the
  forced-only-stub case, it's the one that decides that means `--vad webrtc`:
  ffsubsync would otherwise shift a good subtitle by up to
  `--max-offset-seconds` with a negative score
  ([smacke/ffsubsync#238](https://github.com/smacke/ffsubsync/issues/238)).
  Every other report - no embedded text subtitle at all, or at least one
  full/non-forced one - leaves ffsubsync's own default alone. An explicit
  `--vad ...` in `FFSUBSYNC_EXTRA_ARGS` always wins over what the sidecar
  would otherwise decide, so you can override the backend globally
  regardless: `--vad auditok` is a CPU-only alternative if webrtc struggles
  on a particular library, and `--vad subs_then_webrtc` pins ffsubsync's own
  default even for a forced-only-stub item, if you'd rather have that. The
  `silero` and `fused*` backends need the optional `torch` dependency, which
  the published sidecar image does not install (no GPU base image, per the
  sidecar's Dockerfile) - they'll fail on an unmodified image.

  **Version skew:** the plugin's per-job report needs both a plugin and a
  sidecar new enough to speak it - an older sidecar ignores the field
  entirely (ffsubsync's own default applies, forced-only stubs and all), and
  an older plugin never sends it (same result). Nothing breaks either way,
  the forced-only-stub handling just doesn't kick in until both sides are
  upgraded.
- **`--pgs-ref-stream` - align against a PGS (image-based) subtitle track.**
  Also never added by the sidecar unless the plugin's report calls for it.
  PGS/VobSub/DVB subtitle streams are invisible to ffsubsync's own default
  text-subtitle comparison (see above), so without this flag a video whose
  only usable embedded subtitle is PGS silently falls back to plain audio
  VAD - not wrong, just a missed opportunity, since `--pgs-ref-stream`
  derives timing from *when* each subtitle image is displayed (MKV packet
  timing via `ffprobe`) with **no OCR involved at all**, and is generally as
  reliable as a text-subtitle reference. The plugin reports this only when
  there's no full text track *and* exactly one embedded PGS stream that
  isn't forced - deliberately conservative: ffsubsync's bare
  `--pgs-ref-stream` auto-detects "the first" PGS track by container order,
  so with two or more PGS streams present (say, a forced-only PGS stub
  alongside a full one) the plugin can't guarantee which one that means, and
  reports nothing rather than risk aligning against the wrong one - same
  reasoning as the original forced-only-text-stub problem, just for PGS.
  In that unambiguous case, the sidecar adds bare `--pgs-ref-stream`, which
  is enough since there's only one candidate to auto-detect. An explicit
  `--vad ...`, `--pgs-ref-stream ...` (or its alias `--pgsstream`), or
  `--reference-stream ...` (or its aliases `--refstream`/
  `--reference-track`/`--reftrack`) already in `FFSUBSYNC_EXTRA_ARGS` always
  wins - nothing is added on top of your own choice. VobSub (`dvd_subtitle`)
  and DVB (`dvb_subtitle`/`dvb_teletext`) subtitle streams have no equivalent
  flag in ffsubsync today - they're excluded from the text comparison the
  same way PGS is, but there's nothing for the sidecar to point ffsubsync at
  for them either, so they behave exactly like "no embedded subtitle."
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
