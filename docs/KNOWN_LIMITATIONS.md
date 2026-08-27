# Known limitations

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
