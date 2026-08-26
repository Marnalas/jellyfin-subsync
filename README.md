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
| [Configuration](docs/CONFIGURATION.md) | Job timeout vs queue wait timeout budgets |
| [Known limitations](docs/KNOWN_LIMITATIONS.md) | What the plugin can't do and why |
| [Breaking changes](docs/BREAKING_CHANGES.md) | Upgrade notes per version |
| [Development](docs/DEVELOPMENT.md) | Running the plugin/sidecar test suites |

## Roadmap

- Add a feature in config pages to sync a single video file
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
