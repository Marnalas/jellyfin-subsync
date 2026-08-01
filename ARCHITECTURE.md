# Subsync automation (Option B: sidecar + Jellyfin plugin)

## Architecture

Matches whisper-subs: scheduled sweep (daily + on startup) + skip-cache +
manual "Run Now" trigger. No filesystem watcher, no instant trigger.

```
   ┌─────────────────────────────┐
   │  Jellyfin container          │
   │  ┌─────────────────────────┐ │      HTTP (POST /sync, GET /jobs/x)
   │  │ Subsync plugin           │─┼──────────────────┐
   │  │  - Scheduled sweep task  │ │                   │
   │  │    (daily + startup,     │ │                   ▼
   │  │    also = manual trigger)│ │        ┌───────────────────────┐
   │  │  - skip-cache            │ │        │  subsync-sidecar       │
   │  │  - admin config page     │ │        │  (own container,       │
   │  └─────────────────────────┘ │        │  lightweight Python    │
   └─────────────────────────────┘         │  base, no GPU - plain  │
                                            │  ffsubsync, default    │
                                            │  webrtc VAD, exactly   │
                                            │  what subsync.sh does) │
                                            └───────────────────────┘
```

No docker socket anywhere. No Jellyfin image fork. No GPU reservation - the
default `webrtc` VAD ffsubsync uses is CPU-only, so there's nothing for a
GPU to accelerate right now.

## Build order

1. **`sidecar/`** - build and bring this up first, test it standalone with
   `curl` (see the compose snippet's comments). This is a normal Python
   service; nothing plugin-specific about it yet.
2. **`plugin/`** - once the sidecar's `/health` and `/sync` work from the
   command line, build the plugin against the official Jellyfin plugin
   template and point it at the sidecar's compose service name.

## Known gaps / things you'll need to adjust for your exact setup

- **Path translation**: `WatchedPaths` (Jellyfin-side) and the sidecar's
  `BASE_PATH` mounts need to agree on the same underlying host
  directories, just possibly under different in-container paths. Double
  check `JellyfinMediaRoot`/`SidecarMediaRoot` in the plugin config against
  your actual compose mounts before relying on this.
- **GPU is intentionally out of the picture for now**: this baseline
  matches `subsync.sh` exactly (default `webrtc` VAD, no torch, no CUDA
  base image, no device passthrough). If you switch to GPU-accelerated
  `silero`/`fused` VAD later, that's the point to revisit `sidecar/Dockerfile`
  (back to an `nvidia/cuda` base + `torch`) and `docker-compose.snippet.yml`
  (add back a GPU reservation) - deliberately deferred so we're not
  debugging two things at once.
- **Plugin skeleton is unverified** (no dotnet SDK / network in this
  sandbox to compile against real Jellyfin packages) - see
  `plugin/README.md` for what to check before it'll build.
