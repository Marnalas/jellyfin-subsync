# Jellyfin Subsync

[![CI Status](https://github.com/Marnalas/jellyfin-subsync/actions/workflows/build-release.yml/badge.svg)](https://github.com/Marnalas/jellyfin-subsync/actions/workflows/build-release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-maroon.svg)](https://opensource.org/licenses/MIT)
[![Release](https://img.shields.io/github/v/release/Marnalas/jellyfin-subsync)](https://github.com/Marnalas/jellyfin-subsync/releases)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.0%2B-00A4DC.svg)](https://jellyfin.org)
[![JellyWatch Hub](https://jellywatch.app/hub/subsync-starter/badge.svg)](https://jellywatch.app/hub/subsync-starter)
[![Downloads](https://img.shields.io/github/downloads/Marnalas/jellyfin-subsync/total)](https://github.com/Marnalas/jellyfin-subsync/releases)

![](https://raw.githubusercontent.com/Marnalas/jellyfin-subsync/main/subsyncstarter-banner.png)

A Jellyfin plugin that automatically re-syncs out-of-sync subtitles against
their video, using [ffsubsync](https://github.com/smacke/ffsubsync). It's
made of two pieces:

- **The plugin** (`Jellyfin.Subsync.Starter/`) - runs inside Jellyfin. A scheduled task
  walks the video items in your Jellyfin libraries and syncs the external
  subtitle files (`.srt`, `.ass`, `.ssa`, `.vtt`, `.sub` by default -
  configurable) Jellyfin has already indexed against them. Which subtitle
  belongs to which video is Jellyfin's own answer, not a filename guess, so
  `.forced`, `.sdh` and `pt-BR` style tags work exactly as well as plain
  `.en`. Anything already synced is skipped (tracked by content hash, so
  repeat sweeps are cheap). It also adds a "Run Now" trigger under
  Dashboard > Scheduled Tasks, and an admin config page under Dashboard >
  Plugins > Subsync.
- **`subsync-sidecar/`** - a small always-on HTTP service, run as its own
  Docker container, that wraps `ffsubsync`. The plugin calls it over the
  network to do the actual sync work, so no Docker socket or extra
  privileges are needed inside the Jellyfin container itself.

```
   ┌─────────────────────────────┐
   │  Jellyfin container          │
   │  ┌─────────────────────────┐ │      HTTP (POST /sync, GET /jobs/x)
   │  │ Subsync plugin           │─┼──────────────────┐
   │  │  - Scheduled sweep task  │ │                   ▼
   │  │  - skip-cache            │ │        ┌───────────────────────┐
   │  │  - admin config page     │ │        │  subsync-sidecar       │
   │  └─────────────────────────┘ │        │  (own container, runs  │
   └─────────────────────────────┘         │  ffsubsync + ffmpeg)   │
                                            └───────────────────────┘
```

No GPU is required - the default `webrtc` VAD `ffsubsync` uses is CPU-only.

## Installing

### 1. Sidecar

The sidecar is run as its own container, alongside Jellyfin, in the **same
`docker-compose.yml` that already defines your `jellyfin` service** - it
is not a standalone stack with its own compose file.

Since 3.0.0.0 the image is published to Docker Hub as
[`marnalas/jellyfin-subsync-sidecar`](https://hub.docker.com/r/marnalas/jellyfin-subsync-sidecar),
so there's nothing to build locally - see [Breaking changes](#breaking-changes)
if you're upgrading from 2.1.0.0 or earlier.

Use `subsync-sidecar/compose.yml` as a template: copy the service block it
contains into your own compose file (see below).

Here's an example of the `jellyfin` and sidecar service declarations (env
vars like `${PUID}`/`${MEDIADIR}` are just placeholders, substitute your
own values/volumes):

```yaml
services:
  jellyfin:
    container_name: jellyfin
    image: jellyfin/jellyfin:latest
    restart: unless-stopped
    user: "${PUID}:${PGID}"
    ports:
      - 8096:8096
    volumes:
      - ${DOCKERDIR}/jellyfin/config:/config
      - ${DOCKERDIR}/jellyfin/cache:/cache
      - ${MEDIADIR}/library1:/media/library1
      - ${MEDIADIR}/library2:/media/library2
      - ${MEDIADIR}/library3:/media/library3

  # Pulled from Docker Hub - nothing to build locally. The name you give this
  # service is what you'll use as the host in the plugin's Sidecar URL (here,
  # http://jellyfin-subsync:8000) - it just needs to be consistent between the two.
  jellyfin-subsync:
    image: marnalas/jellyfin-subsync-sidecar:latest
    container_name: jellyfin-subsync
    restart: unless-stopped
    user: "${PUID}:${PGID}"   # match jellyfin's user so both can read/write the same files
    # ports:
    #   - 8420:8000   # only needed to curl it from the host; jellyfin reaches
    #                 # it over the internal docker network either way
    environment:
      # Any extra arg you want added to the ffsubsync commands (e.g.
      # --max-duration-seconds, --extract-audio-first, --multi-segment-sync, etc
      # Parsed with shell quoting rules, so quote any argument containing a
      # space: --vad "webrtc x"
      FFSUBSYNC_EXTRA_ARGS: ""
      # How many sync jobs run at once. Leave empty or set to 0
      # to auto-detect (cpu_count - 1); recommended to set explicitly
      # especially if this container has a `--cpus` limit or shares
      # the host with other CPU-hungry services.
      MAX_PARALLEL_JOBS: 4
      # By default the original subtitle is overwritten with the synced one
      # and no copy is kept. Set to "true" to keep a
      # "<name>_original_backup.srt" copy of the pre-sync subtitle.
      KEEP_ORIGINAL_SUBTITLE_BACKUP: false
      # Optional tuning knobs, shown with their defaults - see
      # "Timeouts and job budgets" below before changing them.
      # JOB_TIMEOUT_SECONDS: 1800        # used only by plugins older than 3.0.0.0
      # MAX_JOB_TIMEOUT_SECONDS: 7200    # ceiling on what the plugin may ask for
      # JOB_RETENTION_SECONDS: 3600      # how long finished jobs stay queryable
      # MAX_JOB_HISTORY: 500             # cap on remembered jobs
    volumes:
      # Mounting each library at the *same* in-container path as jellyfin
      # above means the plugin's WatchedPathsMaps entries can be simple
      # identity maps instead of needing a per-library translation - see
      # the plugin config example below. That being said, you can use
      # different in-container paths on each side if you'd rather keep
      # your own layout, just configure the plugin's Path mappings correctly.
      - ${MEDIADIR}/library1:/media/library1
      - ${MEDIADIR}/library2:/media/library2
      - ${MEDIADIR}/library3:/media/library3
```

Both services need to be reachable from each other, so make sure they're on
the same compose network (the default network they get by simply being in
the same `docker-compose.yml` is enough, unless you've split networks up
elsewhere in that file).

Bring the sidecar up and confirm it's healthy before wiring up the plugin:

```bash
docker compose up -d jellyfin-subsync
# from the host, if you published a port:
curl http://localhost:8420/health
# otherwise, from inside the jellyfin container (the network path the plugin actually uses):
docker compose exec jellyfin curl http://jellyfin-subsync:8000/health
```

### 2. Plugin

Install it like any other third-party Jellyfin plugin, via a repository:

1. Dashboard > Plugins > Repositories > "+" (Add Repository).
2. Repository name: anything, e.g. `Subsync Starter`.
   Repository URL: `https://marnalas.github.io/jellyfin-subsync/manifest.json`
3. Save, then go to Dashboard > Plugins > Catalog, find **Subsync** (category
   "Subtitles"), and click Install.
4. Restart Jellyfin.

Then in Jellyfin, go to Dashboard > Plugins > Subsync and set:

- **Sidecar URL** - e.g. `http://jellyfin-subsync:8000` (the compose service
  name, so it resolves on the internal Docker network).
- **Path mappings** - one library per line, as `jellyfin-path : sidecar-path`
  (e.g. `/path/to/jellyfin/library : /path/in/sidecar/container`). The left
  side is the path as seen inside the Jellyfin container; the right side is
  the same library as seen inside the sidecar container. Each line is
  independent - libraries don't need to share a common root on either side.
  This is translation only - it doesn't choose what gets swept - but every
  library path needs a line here, or its subtitles are skipped with a warning.
- Subtitle extensions and poll interval, if you want anything other than the
  defaults.
- **Job timeout** (default 1800s) and **Queue wait timeout** (default 3600s) -
  the two budgets described under [Timeouts and job
  budgets](#timeouts-and-job-budgets). The defaults suit almost everyone.
- **Sidecar request timeout** (default 30s) - applies to one individual HTTP
  call, not to a sync. Rarely needs changing.
- **Max parallel jobs** - how many subtitles the sweep submits to the
  sidecar at once (default 1). Only raise this alongside the sidecar's own
  `MAX_PARALLEL_JOBS`; the two need to be sized together, see the config
  page's field description.

**The plugin will not sync subtitles until `Sidecar URL` and `Path mappings`
are both configured correctly, and your libraries have been scanned** - if
the URL doesn't resolve/isn't reachable, or a path pair doesn't point at the
same files on both sides, the sweep task just completes having found nothing
to do, silently. For
reference, this is the config that comes out of the fields above when
matching the compose example in step 1, where every library is
mounted at the same in-container path on both sides, so each map entry is
an identity map:

```xml
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <SidecarUrl>http://jellyfin-subsync:8000</SidecarUrl>
  <WatchedPathsMaps>
    <PathMapEntry>
      <JellyfinPath>/media/library1</JellyfinPath>
      <SidecarPath>/media/library1</SidecarPath>
    </PathMapEntry>
    <PathMapEntry>
      <JellyfinPath>/media/library2</JellyfinPath>
      <SidecarPath>/media/library2</SidecarPath>
    </PathMapEntry>
    <PathMapEntry>
      <JellyfinPath>/media/library3</JellyfinPath>
      <SidecarPath>/media/library3</SidecarPath>
    </PathMapEntry>
  </WatchedPathsMaps>
  <SubtitleExtensions>
    <string>srt</string>
    <string>ass</string>
    <string>ssa</string>
    <string>vtt</string>
    <string>sub</string>
  </SubtitleExtensions>
  <PollIntervalMilliseconds>3000</PollIntervalMilliseconds>
  <JobTimeoutSeconds>1800</JobTimeoutSeconds>
  <QueueWaitTimeoutSeconds>3600</QueueWaitTimeoutSeconds>
  <SidecarRequestTimeoutSeconds>30</SidecarRequestTimeoutSeconds>
  <MaxParallelJobs>4</MaxParallelJobs>
</PluginConfiguration>
```

This is the XML Jellyfin persists under your config volume's
`plugins/configurations/` folder after you save the admin page - you
normally never need to touch it by hand, it's shown here just as a
concrete, complete example to check your own settings against.

The sweep's schedule (default: daily at 02:00) is edited separately from
Dashboard > Scheduled Tasks > "Sync unsynced subtitles" > Edit, same as any
other Jellyfin task. That same page also gives you the manual "Run Now"
trigger.

The first run can be expected to run for multiple hours depending on the
number of subtitle files to sync. The task's progress bar shows how much of
the library has been inspected, which is not the same thing as how much time
is left - see the note under [Known limitations](#known-limitations). The
next runs should only sync the newly added subtitle files and therefore be
faster. Execution can be sped up
using MaxParallelJobs in the plugin settings and MAX_PARALLEL_JOBS in the
sidecar container. Be mindful of your hardware capabilities and the other
services running on it.

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

## Roadmap

- Support for Jellyfin 12
- Specific administrator page to force a subtitle file to be (re)synced in the next plugin run
- [open to suggestions]

## Breaking changes

### 3.0.0.0

#### Subtitles come from the Jellyfin library, not a filesystem walk

Up to 2.1.0.0 the plugin walked your watched paths itself and worked out
which subtitle belonged to which video from the filenames, using a pattern
that assumed the tag before the extension was a 2-3 character language code.
That silently never synced anything tagged `.forced` or `.sdh`, anything with
a hyphenated locale like `pt-BR` or `zh-CN`, and anything whose title
happened to end in a short dotted segment (`Show.S01.E02.srt`,
`Movie.4K.srt`) - which is most of what Bazarr and subliminal produce.

From 3.0.0.0 the sweep asks Jellyfin instead. Jellyfin already records every
external subtitle file against the video it belongs to, with its language and
flags resolved, so all of the above now syncs. **You may see a much larger
first run than usual** as the backlog those patterns had been skipping gets
picked up.

What this changes for you:

- **Run a library scan first.** Only subtitles Jellyfin has indexed are
  visible. One that Bazarr dropped in after the last scan won't be synced
  until the next one (Dashboard > Scheduled Tasks > Scan Media Library).
- **"Watched paths" is now path *translation* only**, and is renamed **Path
  mappings** on the config page. It no longer decides what gets swept - your
  libraries do. But every library path still needs an entry: a subtitle whose
  path matches no entry is skipped with a warning, since the sidecar would
  have no way to reach it. Existing configurations keep working unchanged.
- **The "Video extensions" setting is gone.** The video now comes from the
  library item itself, so there was nothing left for it to do. Any saved
  value is ignored and dropped the next time you save the config page.
- **Videos that aren't in a Jellyfin library are no longer touched**, even if
  they sit under a mapped path.
- **Only extensions Jellyfin recognises as subtitles can be picked up**
  (`ass`, `mks`, `sami`, `smi`, `srt`, `ssa`, `sub`, `sup`, `vtt`),
  intersected with your **Subtitle extensions** setting.
- **ISO / BDMV / VIDEO_TS rips are skipped with a warning** - there's no
  single video file for ffsubsync to align against. Previously these were
  attempted and failed at the sidecar.

#### The sidecar image comes from Docker Hub

Up to 2.1.0.0 the sidecar had no published image: you copied
`subsync-sidecar/` next to your compose file and built it yourself.
From 3.0.0.0 the image is built by CI and published to
[`marnalas/jellyfin-subsync-sidecar`](https://hub.docker.com/r/marnalas/jellyfin-subsync-sidecar).

**If you're upgrading, point the service at the published image and delete `subsync-sidecar/`:**

```diff
   jellyfin-subsync:
-    build: ./subsync-sidecar   # path to the folder containing the Dockerfile
+    image: marnalas/jellyfin-subsync-sidecar:latest
```

```bash
docker compose pull jellyfin-subsync
docker compose up -d jellyfin-subsync
```

You can now also drop the local copy of `subsync-sidecar/` - only the
compose service block is still needed.

Available tags:

| Tag | Points at |
| --- | --- |
| `latest` | the newest build of `main` |
| `3.0.0.0`, ... | the sidecar as released alongside that plugin version - pin this if you'd rather upgrade on purpose |
| `sha-<short-sha>` | one specific commit |

Nothing changes on the plugin side: `Sidecar URL`, watched paths, volumes
and env vars all keep working as they did. Building the image yourself
still works too - the `Dockerfile` stays in the repo - it's just no longer
the documented path.

## Testing it

1. Confirm the sidecar is reachable: `curl <SidecarUrl>/health` from inside
   the Jellyfin container. The sweep now checks this itself and **aborts with a
   failed task** if the sidecar doesn't answer, rather than producing one
   timeout per subtitle for the length of the run.
2. Dashboard > Scheduled Tasks > "Scan Media Library" > Run Now, so Jellyfin
   has indexed the subtitle files. The sweep only sees what Jellyfin knows
   about.
3. Dashboard > Scheduled Tasks > "Sync unsynced subtitles" > Run Now. The
   task's progress percentage climbs as the library is walked; watch the
   Jellyfin server log for `Subsync sweep: inspecting N library video item(s)`
   followed by `Subsync: syncing ...` lines.
4. Run it again immediately after - it should finish fast and log nothing,
   since the skip-cache already has everything from step 3.

## Known limitations

- **Subtitles have to be indexed by Jellyfin before they can be synced.**
  The sweep reads the library, not the filesystem, so anything added since
  the last library scan is invisible until the next one. This is the
  trade-off for letting Jellyfin decide which subtitle belongs to which
  video instead of guessing from filenames.
- **Subtitles that don't sit next to their video are skipped**, with a
  warning naming the file. The sidecar's sync endpoint takes a single folder
  plus two filenames, so a cross-directory pair can't be expressed. In
  practice this only affects subtitles Jellyfin stored under its own
  internal metadata folder.
- **ISO, BDMV and VIDEO_TS items are skipped**, with a warning. There's no
  single video file for ffsubsync to align against.
- **The sweep's progress percentage counts library items inspected, not
  subtitles synced.** It's a real percentage of the library walked, but not a
  time estimate: long runs of items with no subtitles fly past, then a single
  item with several subtitles can hold the bar still for minutes. How many
  subtitles the library holds isn't known until the items have been walked,
  and it's the subtitles that take the time. Watch the Jellyfin server log for
  `Subsync: syncing ...` lines if you want visibility into what's actively
  happening.

## Running the tests

The two halves are tested separately, and both run on every push and pull
request (the `test` and `test-sidecar` jobs in `.github/workflows/build-release.yml`).

**Plugin** - xunit, targeting the pure logic: path mapping, the work builder,
the skip-cache, sweep progress, and the plugin's half of the sidecar protocol.

```sh
dotnet test Jellyfin.Subsync.Starter.Tests/Jellyfin.Subsync.Starter.Tests.csproj
```

**Sidecar** - pytest, targeting job bookkeeping: pruning, the cancel paths, the
worker's claim, timeout clamping and temp-file cleanup.

```sh
cd subsync-sidecar
python -m venv .venv && . .venv/bin/activate
pip install -r requirements-dev.txt
pytest
```

`requirements-dev.txt` deliberately doesn't install `ffsubsync`: the suite puts
a stand-in script on `PATH`, which is what makes the failure, timeout and
missing-binary cases reachable at all, and keeps a full run under two seconds.

The two suites meet at `subsync-sidecar/tests/test_contract.py`, which pins the
response shapes that `SubsyncClientTests.cs` hardcodes as fakes. If a test in
there fails, the matching C# fake needs the same edit.

## Thanks

This plugin is just glue - all the actual subtitle-sync work is done by:

- [**ffsubsync**](https://github.com/smacke/ffsubsync) by Stephen Macke,
  the tool that does the actual alignment of a subtitle track to its video.
- [**FFmpeg**](https://ffmpeg.org/), which `ffsubsync` relies on to read
  audio from the video file.

## Disclaimer

AI code generation has been used in the development of this plugin, but no
vide-coding. I write the skeleton and main features and I let AI pipe things
together, write the comments, etc.
