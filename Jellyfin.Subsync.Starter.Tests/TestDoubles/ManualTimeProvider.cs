namespace Jellyfin.Subsync.Starter.Tests.TestDoubles;

/// <summary>
/// A TimeProvider whose clock only moves when a test says so.
/// <para>
/// SubsyncClient's deadlines are the thing most worth testing and the
/// slowest to test for real - "still queued after 59 minutes" is not a wait
/// a test suite can afford. Injecting the clock makes those cases run in
/// microseconds and, more importantly, makes them deterministic rather than
/// dependent on how loaded the CI runner is.
/// </para>
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = start;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, GetUtcNow() + dueTime);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>
    /// True while something is waiting on a timer that hasn't come due.
    /// Tests use it as a synchronisation point: it means the code under
    /// test has finished reacting to the last tick and is asking for the
    /// next one.
    /// </summary>
    public bool HasPendingTimer
    {
        get
        {
            lock (_gate)
            {
                return _timers.Exists(t => t.IsPending);
            }
        }
    }

    public void Advance(TimeSpan amount)
    {
        DateTimeOffset now;
        FakeTimer[] due;
        lock (_gate)
        {
            _now += amount;
            now = _now;
            // Snapshot inside the lock but fire outside it: a callback
            // typically schedules the next timer, which would deadlock on a
            // non-reentrant lock held across the callback.
            due = [.. _timers];
        }

        foreach (var timer in due)
            timer.FireIfDue(now);
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(ManualTimeProvider provider, TimerCallback callback, object? state, DateTimeOffset dueAt)
        : ITimer
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _dueAt = dueAt;
        private bool _fired;
        private bool _disposed;

        public bool IsPending
        {
            get
            {
                lock (_gate)
                {
                    return !_fired && !_disposed;
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;

                _dueAt = provider.GetUtcNow() + dueTime;
                _fired = dueTime == Timeout.InfiniteTimeSpan;
                return true;
            }
        }

        public void FireIfDue(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_fired || _disposed || _dueAt > now)
                    return;

                _fired = true;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }

            provider.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

