# Breaking changes

## 3.0.0.0

### Subtitles come from the Jellyfin library, not a filesystem walk

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

### The sidecar image comes from Docker Hub

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
