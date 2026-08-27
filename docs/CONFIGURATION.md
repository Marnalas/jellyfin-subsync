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
- **`--vad` - swap the voice-activity detector.** The sidecar doesn't set this,
  so whichever backend your installed `ffsubsync` version defaults to is used.
  If it struggles on a particular library (noisy mixes, animation with sparse
  dialogue), `--vad auditok` is a CPU-only alternative worth trying, and
  `--vad subs_then_auditok` seeds the search from an existing (even
  slightly-off) subtitle before falling back to audio. The `silero` and
  `fused*` backends need the optional `torch` dependency, which the published
  sidecar image does not install (no GPU base image, per the sidecar's
  Dockerfile) - they'll fail on an unmodified image.
- **`--skip-sync-on-low-quality` - refuse a low-confidence alignment.** Makes
  ffsubsync leave the subtitle's timing unchanged instead of applying an
  alignment it isn't confident about (tune the threshold with `--min-score`,
  `--quality-max-offset-seconds`, `--max-framerate-deviation`). **Caveat for
  this plugin:** when it triggers, ffsubsync still writes an output file (the
  unchanged original), so the sidecar sees a normal success and the skip-cache
  marks the file synced - it will not be retried on a later sweep even though
  nothing was actually fixed. Clear that item's cache entry from the admin UI
  once you've addressed the subtitle if it deserves another attempt.
- **`--suppress-output-if-offset-less-than` - the opposite caveat.** When the
  computed offset is below the threshold, ffsubsync writes *no* output file at
  all. The sidecar's success check requires that file to exist, so this reads
  as a failed job - combined with the fail-cache, a subtitle that's already
  close enough will accumulate failures and eventually stop being retried,
  which is a harmless end state but shows up as "failed" rather than "skipped"
  in the sync history.
