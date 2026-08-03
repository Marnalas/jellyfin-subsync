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

- **The plugin** (this repo's root) - runs inside Jellyfin. A scheduled task
  sweeps your configured library paths for subtitle files (`.srt`, `.ass`,
  `.ssa`, `.vtt`, `.sub` by default - configurable), matches each one to
  its video, and skips anything already synced (tracked by content hash so
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

The sidecar is built and run as its own container, alongside Jellyfin, in
the **same `docker-compose.yml` that already defines your `jellyfin`
service** - it is not a standalone stack with its own compose file.

Copy every file from this repo's `subsync-sidecar/` directory **except
`compose.yml`** (i.e. `app.py`, `Dockerfile`, `requirements.txt`) into a
folder (e.g. `subsync-sidecar/`) placed next to your own compose file.
`compose.yml` itself is only a template: you don't copy it, you copy the
service block it contains into your own compose file (see below).

That gives you a layout like this:

```
your-stack/                  # wherever your docker-compose.yml lives
├── docker-compose.yml       # already defines `jellyfin`
└── subsync-sidecar/
    ├── app.py
    ├── Dockerfile
    └── requirements.txt
```

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

  # Built from the files copied previously. The name you give this service
  # is what you'll use as the host in the plugin's Sidecar URL (here,
  # http://jellyfin-subsync:8000) - it just needs to be consistent between the two.
  jellyfin-subsync:
    container_name: jellyfin-subsync
    build: ./subsync-sidecar # match the folder name where you copied the sidecar files
    restart: unless-stopped
    user: "${PUID}:${PGID}"   # match jellyfin's user so both can read/write the same files
    # ports:
    #   - 8420:8000   # only needed to curl it from the host; jellyfin reaches
    #                 # it over the internal docker network either way
    environment:
      MAX_PARALLEL_JOBS: 4
      KEEP_ORIGINAL_SUBTITLE_BACKUP: false
    volumes:
      # Mounting each library at the *same* in-container path as jellyfin
      # above means the plugin's WatchedPathsMaps entries can be simple
      # identity maps instead of needing a per-library translation - see
      # the plugin config example below. That being said, you can use
      # different in-container paths on each side if you'd rather keep
      # your own layout, just configure the plugin's Watched paths correctly.
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
- **Watched paths** - one library per line, as `jellyfin-path : sidecar-path`
  (e.g. `/path/to/jellyfin/library : /path/in/sidecar/container`). The left side is the path
  as seen inside the Jellyfin container; the right side is the same library
  as seen inside the sidecar container. Each line is independent -
  libraries don't need to share a common root on either side.
- Video extensions, poll interval, and job timeout, if you want anything
  other than the defaults.
- **Max parallel jobs** - how many subtitles the sweep submits to the
  sidecar at once (default 1). Only raise this alongside the sidecar's own
  `MAX_PARALLEL_JOBS`; the two need to be sized together, see the config
  page's field description.

**The plugin will not sync subtitles until `Sidecar URL` and `Watched paths`
are both configured correctly** - if the URL doesn't resolve/isn't
reachable, or a path pair doesn't point at the same files on both sides,
the sweep task just completes having found nothing to do, silently. For
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
  <VideoExtensions>
    <string>mkv</string>
    <string>mp4</string>
    <string>m4v</string>
    <string>avi</string>
    <string>ts</string>
    <string>mov</string>
    <string>wmv</string>
  </VideoExtensions>
  <PollIntervalMilliseconds>3000</PollIntervalMilliseconds>
  <JobTimeoutSeconds>1800</JobTimeoutSeconds>
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
number of subtitle files to sync. The next runs should only sync the newly
added subtitle files and therefore be faster. Execution can be sped up
using MaxParallelJobs in the plugin settings and MAX_PARALLEL_JOBS in the
sidecar container. Be mindful of your hardware capabilities and the other
services running on it.

## Testing it

1. Confirm the sidecar is reachable: `curl <SidecarUrl>/health` from inside
   the Jellyfin container.
2. Dashboard > Scheduled Tasks > "Sync unsynced subtitles" > Run Now. Watch
   the Jellyfin server log for `Subsync: syncing ...` lines.
3. Run it again immediately after - it should finish fast and log nothing,
   since the skip-cache already has everything from step 2.

## Known limitations

- **The "Sync unsynced subtitles" task shows an indeterminate progress bar,
  not a percentage.** This is intentional, not a bug: subtitle files are
  discovered lazily, streamed one path at a time instead of being collected
  upfront, so a huge library never has to sit fully buffered in memory
  before syncing starts. The trade-off is that the total subtitle count
  isn't known until the sweep finishes, so there's no meaningful number to
  report progress against - an indeterminate bar is preferable to one stuck
  at 0% for the entire run. Watch the Jellyfin server log for `Subsync:
  syncing ...` lines if you want visibility into what's actively happening.

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
