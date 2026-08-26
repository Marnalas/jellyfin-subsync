namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// Turns the sweep's per-item completions into the percentage Jellyfin's
/// task UI wants. The denominator is the library item count, which is known
/// as soon as the id query returns; items are then credited one at a time,
/// whether they were skipped during enumeration or fully processed by the
/// task. Every item has to be credited exactly once by exactly one of those
/// two paths, or the bar stops short of 100%.
/// </summary>
internal sealed class SweepProgress(IProgress<double> progress)
{
    private readonly IProgress<double> _progress = progress;
    private readonly Lock _lock = new();
    private int _total;
    private int _completed;
    private int _lastReported = -1;

    /// <summary>
    /// Sets the denominator and reports 0%, which is what flips the
    /// dashboard's bar from indeterminate to determinate. Called once,
    /// before the first item is credited; until then <see cref="ItemDone"/>
    /// reports nothing.
    /// </summary>
    internal void SetTotal(int total)
    {
        Volatile.Write(ref _total, total);
        Report();
    }

    /// <summary>
    /// Credits one library item as done. Safe to call concurrently - the
    /// sweep calls this from its parallel workers.
    /// </summary>
    internal void ItemDone()
    {
        Interlocked.Increment(ref _completed);
        Report();
    }

    private void Report()
    {
        var total = Volatile.Read(ref _total);
        if (total <= 0)
            return;

        // The comparison and the Report have to happen together: an
        // interleaved lock-free version can win the race to claim 3% and
        // then be descheduled long enough for 4% to be delivered first,
        // which draws a bar that ticks backwards. Contention is a non-issue
        // - at most one call per library item, each of which took a sidecar
        // round-trip to get here.
        lock (_lock)
        {
            // Jellyfin's task UI reads this as a 0-100 percentage. Floored
            // to a whole percent and only reported when it actually moves,
            // so a six-figure library pushes ~100 progress events to the
            // dashboard instead of one per item.
            var percent = (int)Math.Min(100L, Volatile.Read(ref _completed) * 100L / total);
            if (percent <= _lastReported)
                return;

            _lastReported = percent;
            _progress.Report(percent);
        }
    }
}