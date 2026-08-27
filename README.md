# Jellyfin Subsync

[![CI Status](https://github.com/Marnalas/jellyfin-subsync/actions/workflows/build-release.yml/badge.svg)](https://github.com/Marnalas/jellyfin-subsync/actions/workflows/build-release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-maroon.svg)](https://opensource.org/licenses/MIT)
[![Release](https://img.shields.io/github/v/release/Marnalas/jellyfin-subsync)](https://github.com/Marnalas/jellyfin-subsync/releases)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.0%2B-00A4DC.svg)](https://jellyfin.org)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.0%2B-00A4DC.svg)](https://jellyfin.org)
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
  repeat sweeps are cheap), and a subtitle that keeps failing to sync stops
  being retried automatically after a configurable number of consecutive
  failures. It also adds a "Run Now" trigger under
  Dashboard > Scheduled Tasks, and an admin config page under Dashboard >
  Plugins > Subsync.
- **`subsync-sidecar/`** - a small always-on HTTP service, run as its own
  Docker container, that wraps `ffsubsync`. The plugin calls it over the
  network to do the actual sync work, so no Docker socket or extra
  privileges are needed inside the Jellyfin container itself.

```
   ┌─────────────────────────────┐
   │  Jellyfin container         │
   │  ┌─────────────────────────┐│      HTTP (POST /sync, GET /jobs/x)
   │  │ Subsync plugin          │┼──────────────────┐
   │  │  - Scheduled sweep task ││                   ▼
   │  │  - skip/fail-cache      ││        ┌───────────────────────┐
   │  │  - admin config page    ││        │  subsync-sidecar      │
   │  └─────────────────────────┘│        │  (own container, runs │
   └─────────────────────────────┘        │  ffsubsync + ffmpeg)  │
                                          └───────────────────────┘
```

No GPU is required - the default `webrtc` VAD `ffsubsync` uses is CPU-only.

## Features

- Automatically re-syncs out-of-sync external subtitles against their video
  on a schedule, using Jellyfin's own subtitle-to-video pairing (not
  filename guessing) - so `.forced`, `.sdh`, `pt-BR`-style tags etc. all
  work correctly.
- Skip-cache tracks what's already synced (by content hash) so repeat
  sweeps only do new work; stale entries for deleted files are pruned
  automatically.
- Fail-cache stops retrying a subtitle that fails to sync too many times
  in a row (configurable, default 3), so a permanently broken file doesn't
  get reprocessed on every sweep forever. Resets automatically if the
  file's content changes.
- On-demand sync of a single movie or episode, without waiting for the
  next scheduled sweep.
- Cache management from the admin UI: clear the whole skip-cache and
  fail-cache, or just one item (e.g. after manually replacing a subtitle).
- Supports multiple subtitle formats (`.srt`, `.ass`, `.ssa`, `.vtt`,
  `.sub` by default, configurable) and preserves the original format
  rather than converting.
- Per-library path mapping between Jellyfin's library paths and the
  sidecar's view of the filesystem.
- Configurable throughput and timing: max parallel jobs, job/queue-wait
  timeouts, poll interval.
- Integrates with Jellyfin's native scheduled-task system: manual "Run
  Now", progress reporting, standard scheduling UI.
- Suppresses Jellyfin's library file-watcher on a subtitle's folder while
  the synced file is being written, so Jellyfin doesn't redownload/overwrite
  the subtitle that was just synced.
- Sync work runs in a separate sidecar service, so the Jellyfin container
  itself needs no Docker socket or extra privileges.
- CPU-only - no GPU required.

## Quick start

1. **Sidecar** - add the `jellyfin-subsync` service to the same
   `docker-compose.yml` your `jellyfin` service already lives in, using the
   published image `marnalas/jellyfin-subsync-sidecar` (Docker Hub). Bring
   it up and confirm `/health` responds before moving on.
2. **Plugin** - add repository `https://marnalas.github.io/jellyfin-subsync/manifest.json`
   under Dashboard > Plugins > Repositories, install **Subsync** from the
   Catalog, restart Jellyfin, then set **Sidecar URL** and **Path mappings**
   on Dashboard > Plugins > Subsync.

**The plugin won't sync anything until `Sidecar URL` and `Path mappings` are
both configured correctly and your libraries have been scanned** - a bad
config just makes the sweep complete having found nothing to do, silently.

Full compose example, field-by-field config walkthrough, and a post-install
smoke test are in [Installation](docs/INSTALLATION.md).

## Documentation

| Doc | Covers |
| --- | --- |
| [Installation](docs/INSTALLATION.md) | Full sidecar + plugin setup, config reference, XML example, post-install smoke test |
| [Configuration](docs/CONFIGURATION.md) | Job timeout vs queue wait timeout budgets, useful `FFSUBSYNC_EXTRA_ARGS` flags |
| [Known limitations](docs/KNOWN_LIMITATIONS.md) | What the plugin can't do and why |
| [Breaking changes](docs/BREAKING_CHANGES.md) | Upgrade notes per version |
| [Development](docs/DEVELOPMENT.md) | Running the plugin/sidecar test suites |

## Roadmap

- [open to suggestions]

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
