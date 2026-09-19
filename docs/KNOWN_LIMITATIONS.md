# Known limitations

- **Subtitles have to be indexed by Jellyfin before they can be synced.**
  The sweep reads the library, not the filesystem, so anything added since
  the last library scan is invisible until the next one. This is the
  trade-off for letting Jellyfin decide which subtitle belongs to which
  video instead of guessing from filenames.
- **Forced-subtitle handling trusts the container's disposition tags, with
  no way to double-check them.** Whether an embedded or external subtitle
  counts as "forced" comes straight from Jellyfin's own `IsForced` flag,
  which in turn comes from the file's ffprobe `disposition.forced` tag - the
  plugin never inspects subtitle content, line count, or duration to verify
  it. If a container has that tag wrong, the plugin gets it wrong too, with
  no fallback: a full/translation track mistagged as forced looks like a
  forced-only stub, so alignment falls back to (less accurate) audio VAD
  instead of using the track directly; a genuinely forced stub
  mistagged as not-forced looks like a real full track and, if picked as
  the alignment reference, can leave ffsubsync aligning against a
  near-empty stream. There's no fix on the plugin side for this - if syncs
  or forced-subtitle badges look wrong for a specific file, check and
  correct its disposition tags (e.g. by re-muxing) at the source or make sure
  to use releases made by teams that correctly set these tags.
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
