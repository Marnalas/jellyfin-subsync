# Jellyfin Subsync

A Jellyfin plugin that automatically re-syncs out-of-sync subtitles against
their video, using [ffsubsync](https://github.com/smacke/ffsubsync). It's
made of two pieces:

- **The plugin** (this repo's root) - runs inside Jellyfin. A scheduled task
  sweeps your configured library paths for `.srt` files, matches each one to
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

Build and run `subsync-sidecar/` as its own container alongside Jellyfin.
Add it to the same `docker-compose.yml` that defines your `jellyfin`
service; see `subsync-sidecar/docker-compose.snippet.yml` for a template:

```yaml
  subsync-sidecar:
    container_name: subsync-sidecar
    build: ./subsync-sidecar
    restart: unless-stopped
    environment:
      BASE_PATH: /mnt/media
      FFSUBSYNC_EXTRA_ARGS: ""
    volumes:
      - /path/to/library1:/mnt/media/library1
      - /path/to/library2:/mnt/media/library2
    ports:
      - 8420:8000
    networks:
      - default   # must be the same compose network jellyfin is on
```

Bring it up and confirm it's healthy before wiring up the plugin:

```bash
docker compose up -d subsync-sidecar
curl http://localhost:8420/health
```

### 2. Plugin

```bash
dotnet build --configuration Release
mkdir -p <jellyfin-config>/plugins/Subsync
cp bin/Release/net9.0/Jellyfin.Subsync.Starter.dll <jellyfin-config>/plugins/Subsync/
# restart jellyfin
```

Then in Jellyfin, go to Dashboard > Plugins > Subsync and set:

- **Sidecar URL** - e.g. `http://subsync-sidecar:8000` (the compose service
  name, so it resolves on the internal Docker network).
- **Watched paths** - library paths to scan, as seen inside the Jellyfin
  container (e.g. `/media/films`, `/media/series`).
- **Jellyfin media root / Sidecar media root** - the common path prefix on
  each side, used to translate a Jellyfin-side path into the folder the
  sidecar expects (e.g. `/media` -> `/mnt/media`). These need to point at
  the same underlying host directories as your compose volume mounts.
- Video extensions, poll interval, and job timeout, if you want anything
  other than the defaults.

The sweep's schedule (default: daily at 02:00, plus once on every server
startup) is edited separately from Dashboard > Scheduled Tasks > "Sync
unsynced subtitles" > Edit, same as any other Jellyfin task. That same page
also gives you the manual "Run Now" trigger.

## Testing it

1. Confirm the sidecar is reachable: `curl <SidecarUrl>/health` from inside
   the Jellyfin container.
2. Dashboard > Scheduled Tasks > "Sync unsynced subtitles" > Run Now. Watch
   the Jellyfin server log for `Subsync: syncing ...` lines.
3. Run it again immediately after - it should finish fast and log nothing,
   since the skip-cache already has everything from step 2.

## Thanks

This plugin is just glue - all the actual subtitle-sync work is done by:

- [**ffsubsync**](https://github.com/smacke/ffsubsync) by Stephen Macke,
  the tool that does the actual alignment of a subtitle track to its video.
- [**FFmpeg**](https://ffmpeg.org/), which `ffsubsync` relies on to read
  audio from the video file.
