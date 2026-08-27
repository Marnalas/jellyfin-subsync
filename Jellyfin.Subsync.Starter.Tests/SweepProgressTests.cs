using Jellyfin.Subsync.Starter.Infrastructure;
using Xunit;

namespace Jellyfin.Subsync.Starter.Tests;

/// <summary>
/// <see cref="SweepProgress"/> is what turns the sweep's per-item
/// completions into the percentage the Jellyfin dashboard draws. Two
/// properties matter enough to pin down: a bar that goes backwards or
/// overshoots reads as a bug to anyone watching it, and one report per item
/// on a six-figure library would flood the websocket the dashboard listens
/// on. Both callers (the enumeration crediting skipped items, the task's
/// parallel workers crediting processed ones) hit this concurrently, so the
/// concurrent case is tested the way it's actually called.
/// </summary>
public class SweepProgressTests
{
    /// <summary>
    /// Collects every value handed to <see cref="IProgress{T}.Report"/>.
    /// Locked because the real sweep reports from parallel workers.
    /// </summary>
    private sealed class RecordingProgress : IProgress<double>
    {
        private readonly Lock _lock = new();
        private readonly List<double> _values = [];

        public void Report(double value)
        {
            lock (_lock)
            {
                _values.Add(value);
            }
        }

        public IReadOnlyList<double> Values
        {
            get
            {
                lock (_lock)
                {
                    return [.. _values];
                }
            }
        }
    }

    private static (SweepProgress Progress, RecordingProgress Recorder) Build(int? total = null)
    {
        var recorder = new RecordingProgress();
        var progress = new SweepProgress(recorder);
        if (total.HasValue)
            progress.SetTotal(total.Value);

        return (progress, recorder);
    }

    // --- A. Nothing to divide by ---

    /// <summary>
    /// The enumeration sets the total on its first MoveNext, which happens
    /// before anything can be credited - but a divide-by-zero here would
    /// take down the whole sweep, so the guard is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void TotalNeverSet_ReportsNothing()
    {
        var (progress, recorder) = Build();

        progress.ItemDone();
        progress.ItemDone();

        Assert.Empty(recorder.Values);
    }

    /// <summary>
    /// An empty library (or one where the query matches nothing) reports
    /// nothing at all; the task's own final Report(100) is what leaves the
    /// bar full in that case.
    /// </summary>
    [Fact]
    public void ZeroItems_ReportsNothing()
    {
        var (progress, recorder) = Build(total: 0);

        progress.ItemDone();

        Assert.Empty(recorder.Values);
    }

    // --- B. The percentage itself ---

    [Fact]
    public void EveryItemCompleted_EndsAtOneHundred()
    {
        var (progress, recorder) = Build(total: 250);

        for (var i = 0; i < 250; ++i)
            progress.ItemDone();

        Assert.Equal(100d, recorder.Values[^1]);
    }

    /// <summary>
    /// Rounding is down, not nearest: crediting 3 of 8 items must never
    /// claim 38% has been reached when it hasn't.
    /// </summary>
    [Theory]
    // Exact division, no rounding involved.
    [InlineData(4, 1, 25d)]
    [InlineData(4, 3, 75d)]
    // 3/8 is 37.5%, which has to floor rather than round up to 38.
    [InlineData(8, 3, 37d)]
    // A single item out of three is 33.3%.
    [InlineData(3, 1, 33d)]
    public void PartialCompletion_ReportsFlooredPercentage(int total, int completed, double expected)
    {
        var (progress, recorder) = Build(total);

        for (var i = 0; i < completed; ++i)
            progress.ItemDone();

        Assert.Equal(expected, recorder.Values[^1]);
    }

    // --- C. Not flooding the dashboard ---

    /// <summary>
    /// One report per item on a large library is one websocket push per
    /// item. Whole-percent throttling is what keeps that bounded, so the
    /// bound is asserted rather than the individual values: the opening 0%
    /// from SetTotal plus one report per percent from 1 to 100.
    /// </summary>
    [Fact]
    public void LargeLibrary_ReportsAtMostOncePerWholePercent()
    {
        var (progress, recorder) = Build(total: 10_000);

        for (var i = 0; i < 10_000; ++i)
            progress.ItemDone();

        Assert.Equal(101, recorder.Values.Count);
    }

    /// <summary>
    /// Setting the denominator reports 0% on its own - that first report is
    /// what tells the dashboard to draw a percentage bar instead of the
    /// indeterminate marquee, so it can't wait for the first completed item.
    /// </summary>
    [Fact]
    public void SettingTotal_ImmediatelyReportsZero()
    {
        var (_, recorder) = Build(total: 40);

        Assert.Equal([0d], recorder.Values);
    }

    [Fact]
    public void ReportsAreStrictlyIncreasing()
    {
        var (progress, recorder) = Build(total: 777);

        for (var i = 0; i < 777; ++i)
            progress.ItemDone();

        var values = recorder.Values;
        for (var i = 1; i < values.Count; ++i)
            Assert.True(values[i] > values[i - 1], $"report {i} ({values[i]}) did not advance past {values[i - 1]}");
    }

    // --- D. Miscounting shouldn't produce a nonsense bar ---

    /// <summary>
    /// Every item is supposed to be credited exactly once, by either the
    /// enumeration or the task. If a future change double-credits one, the
    /// bar should saturate rather than report 140%.
    /// </summary>
    [Fact]
    public void MoreCompletionsThanTotal_NeverExceedsOneHundred()
    {
        var (progress, recorder) = Build(total: 10);

        for (var i = 0; i < 25; ++i)
            progress.ItemDone();

        Assert.All(recorder.Values, value => Assert.True(value <= 100d, $"reported {value}"));
        Assert.Equal(100d, recorder.Values[^1]);
    }

    // --- E. How the sweep actually calls it ---

    [Fact]
    public void ConcurrentItemDone_EndsAtOneHundredWithNoDuplicates()
    {
        const int Total = 5_000;
        var (progress, recorder) = Build(Total);

        Parallel.For(0, Total, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => progress.ItemDone());

        var values = recorder.Values;
        Assert.Equal(100d, values[^1]);
        Assert.Equal(values.Count, values.Distinct().Count());
        for (var i = 1; i < values.Count; ++i)
            Assert.True(values[i] > values[i - 1], $"report {i} ({values[i]}) did not advance past {values[i - 1]}");
    }
}