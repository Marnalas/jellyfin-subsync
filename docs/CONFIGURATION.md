# Configuration

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
