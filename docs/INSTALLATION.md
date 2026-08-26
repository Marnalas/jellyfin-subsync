# Installation

## 1. Sidecar

The sidecar is run as its own container, alongside Jellyfin, in the **same
`docker-compose.yml` that already defines your `jellyfin` service** - it
is not a standalone stack with its own compose file.

Since 3.0.0.0 the image is published to Docker Hub as
[`marnalas/jellyfin-subsync-sidecar`](https://hub.docker.com/r/marnalas/jellyfin-subsync-sidecar),
so there's nothing to build locally - see [Breaking changes](BREAKING_CHANGES.md)
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
      # "Timeouts and job budgets" (docs/CONFIGURATION.md) before changing them.
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

## 2. Plugin

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
- **Path mappings** - the config page lists your actual Jellyfin libraries
  (fetched live from the server). Each one is **disabled by default**; toggle
  on the ones you want swept, then fill in the sidecar-side path for each of
  that library's folders (shown read-only, pulled straight from the library
  itself - you never type the Jellyfin-side path). This is translation
  only - it doesn't choose what gets swept - but every enabled library's
  folders need a sidecar path filled in, or its subtitles are skipped with a
  warning. Upgrading from an older version that used the old free-text path
  mapping field pre-fills this list automatically from your existing
  mappings; nothing changes until you save.
- Subtitle extensions and poll interval, if you want anything other than the
  defaults.
- **Job timeout** (default 1800s) and **Queue wait timeout** (default 3600s) -
  the two budgets described under [Timeouts and job
  budgets](CONFIGURATION.md). The defaults suit almost everyone.
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
matching the compose example in step 1, where every library is mounted at
the same in-container path on both sides (so each mapping is an identity
map) and all three libraries have been toggled on (they start disabled).
`LibraryPathMappings` is what the config page actually saves from your
edits; `WatchedPathsMaps` is derived from it automatically on save (only
enabled libraries contribute) and is what the sweep task reads:

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
  <LibraryPathMappings>
    <LibraryPathMapping>
      <LibraryId>8f14e45f-ceea-4d3a-b1e4-000000000001</LibraryId>
      <LibraryName>library1</LibraryName>
      <Enabled>true</Enabled>
      <PathMappings>
        <PathMapEntry>
          <JellyfinPath>/media/library1</JellyfinPath>
          <SidecarPath>/media/library1</SidecarPath>
        </PathMapEntry>
      </PathMappings>
    </LibraryPathMapping>
    <LibraryPathMapping>
      <LibraryId>8f14e45f-ceea-4d3a-b1e4-000000000002</LibraryId>
      <LibraryName>library2</LibraryName>
      <Enabled>true</Enabled>
      <PathMappings>
        <PathMapEntry>
          <JellyfinPath>/media/library2</JellyfinPath>
          <SidecarPath>/media/library2</SidecarPath>
        </PathMapEntry>
      </PathMappings>
    </LibraryPathMapping>
    <LibraryPathMapping>
      <LibraryId>8f14e45f-ceea-4d3a-b1e4-000000000003</LibraryId>
      <LibraryName>library3</LibraryName>
      <Enabled>true</Enabled>
      <PathMappings>
        <PathMapEntry>
          <JellyfinPath>/media/library3</JellyfinPath>
          <SidecarPath>/media/library3</SidecarPath>
        </PathMapEntry>
      </PathMappings>
    </LibraryPathMapping>
  </LibraryPathMappings>
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
is left - see the note under [Known limitations](KNOWN_LIMITATIONS.md). The
next runs should only sync the newly added subtitle files and therefore be
faster. Execution can be sped up
using MaxParallelJobs in the plugin settings and MAX_PARALLEL_JOBS in the
sidecar container. Be mindful of your hardware capabilities and the other
services running on it.

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
