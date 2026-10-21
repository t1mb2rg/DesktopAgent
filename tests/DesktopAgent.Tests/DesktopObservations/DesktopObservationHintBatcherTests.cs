using DesktopAgent.Application.DesktopObservations;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopObservationHintBatcherTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Settings_InvalidRanges_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopObservationHintBatchSettings(
                TimeSpan.FromMilliseconds(-1),
                TimeSpan.FromSeconds(1)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopObservationHintBatchSettings(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(-1)));

        Assert.Throws<ArgumentException>(() =>
            new DesktopObservationHintBatchSettings(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RegisterHint_FirstHint_UsesQuietPeriodAsDueTime()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);

        var registration = batcher.RegisterHint(
            Hint(DesktopObservationHintKind.FileSystem, "fs", T0));

        Assert.Null(registration.ReadyBatch);
        Assert.Equal(T0.AddMilliseconds(500), registration.PendingDueAtUtc);
        Assert.Equal(registration.PendingDueAtUtc, batcher.NextDueAtUtc);
    }

    [Fact]
    public void RegisterHint_RepeatedHints_ExtendQuietPeriod()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);

        batcher.RegisterHint(Hint(DesktopObservationHintKind.FileSystem, "fs", T0));
        var registration = batcher.RegisterHint(
            Hint(DesktopObservationHintKind.Shell, "shell", T0.AddMilliseconds(300)));

        Assert.Equal(T0.AddMilliseconds(800), registration.PendingDueAtUtc);
    }

    [Fact]
    public void RegisterHint_EventStorm_IsCappedByMaxBatchDelay()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 1200);

        batcher.RegisterHint(Hint(DesktopObservationHintKind.FileSystem, "fs", T0));
        batcher.RegisterHint(Hint(DesktopObservationHintKind.FileSystem, "fs", T0.AddMilliseconds(400)));
        batcher.RegisterHint(Hint(DesktopObservationHintKind.Shell, "shell", T0.AddMilliseconds(800)));
        var registration = batcher.RegisterHint(
            Hint(DesktopObservationHintKind.DisplayTopology, "display", T0.AddMilliseconds(1100)));

        Assert.Equal(T0.AddMilliseconds(1200), registration.PendingDueAtUtc);
    }

    [Fact]
    public void TryTakeReady_BeforeDue_ReturnsNull_AtDueReturnsBatch()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);
        batcher.RegisterHint(Hint(DesktopObservationHintKind.FileSystem, "fs", T0));

        Assert.Null(batcher.TryTakeReady(T0.AddMilliseconds(499)));

        var batch = Assert.IsType<DesktopObservationHintBatch>(
            batcher.TryTakeReady(T0.AddMilliseconds(500)));

        Assert.Equal(1, batch.TotalHintCount);
        Assert.Equal(T0, batch.FirstHintAtUtc);
        Assert.Equal(T0, batch.LastHintAtUtc);
        Assert.Equal(T0.AddMilliseconds(500), batch.DueAtUtc);
        Assert.Null(batcher.NextDueAtUtc);
    }

    [Fact]
    public void RegisterHint_AfterOldBatchDue_FlushesOldAndStartsNewBatch()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);
        batcher.RegisterHint(Hint(DesktopObservationHintKind.FileSystem, "fs", T0));

        var nextTime = T0.AddMilliseconds(700);
        var registration = batcher.RegisterHint(
            Hint(DesktopObservationHintKind.Shell, "shell", nextTime));

        var ready = Assert.IsType<DesktopObservationHintBatch>(registration.ReadyBatch);
        Assert.Equal(1, ready.TotalHintCount);
        Assert.Equal(1, ready.HintCounts[DesktopObservationHintKind.FileSystem]);

        Assert.Equal(nextTime.AddMilliseconds(500), registration.PendingDueAtUtc);
        var newBatch = Assert.IsType<DesktopObservationHintBatch>(
            batcher.TryTakeReady(nextTime.AddMilliseconds(500)));
        Assert.Equal(1, newBatch.TotalHintCount);
        Assert.Equal(1, newBatch.HintCounts[DesktopObservationHintKind.Shell]);
    }

    [Fact]
    public void ReadyBatch_AggregatesKindCountsAndDistinctSourcesDeterministically()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);
        batcher.RegisterHint(Hint(DesktopObservationHintKind.Shell, "z-shell", T0));
        batcher.RegisterHint(Hint(DesktopObservationHintKind.FileSystem, "a-fs", T0.AddMilliseconds(50)));
        batcher.RegisterHint(Hint(DesktopObservationHintKind.Shell, "z-shell", T0.AddMilliseconds(100)));

        var batch = Assert.IsType<DesktopObservationHintBatch>(
            batcher.TryTakeReady(T0.AddMilliseconds(600)));

        Assert.Equal(3, batch.TotalHintCount);
        Assert.Equal(2, batch.HintCounts[DesktopObservationHintKind.Shell]);
        Assert.Equal(1, batch.HintCounts[DesktopObservationHintKind.FileSystem]);
        Assert.Equal(new[] { "a-fs", "z-shell" }, batch.Sources);
    }

    [Fact]
    public void RegisterHint_OutOfOrderTimestamp_DoesNotMoveBatchTimeBackward()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);
        batcher.RegisterHint(Hint(DesktopObservationHintKind.Shell, "shell", T0));

        var registration = batcher.RegisterHint(
            Hint(DesktopObservationHintKind.FileSystem, "fs", T0.AddSeconds(-5)));

        Assert.Equal(T0.AddMilliseconds(500), registration.PendingDueAtUtc);
        var batch = Assert.IsType<DesktopObservationHintBatch>(
            batcher.TryTakeReady(T0.AddMilliseconds(500)));
        Assert.Equal(T0, batch.LastHintAtUtc);
        Assert.Equal(2, batch.TotalHintCount);
    }

    [Fact]
    public void TakePendingNow_DrainsWithoutWaiting()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);
        batcher.RegisterHint(Hint(DesktopObservationHintKind.Manual, "manual", T0));

        var batch = Assert.IsType<DesktopObservationHintBatch>(batcher.TakePendingNow());

        Assert.Equal(1, batch.TotalHintCount);
        Assert.Null(batcher.NextDueAtUtc);
        Assert.Null(batcher.TakePendingNow());
    }

    [Fact]
    public void Reset_DropsPendingBatch()
    {
        var batcher = Batcher(quietMs: 500, maxMs: 3000);
        batcher.RegisterHint(Hint(DesktopObservationHintKind.FileSystem, "fs", T0));

        batcher.Reset();

        Assert.Null(batcher.NextDueAtUtc);
        Assert.Null(batcher.TryTakeReady(T0.AddHours(1)));
    }

    private static DesktopObservationHintBatcher Batcher(int quietMs, int maxMs) =>
        new(new DesktopObservationHintBatchSettings(
            TimeSpan.FromMilliseconds(quietMs),
            TimeSpan.FromMilliseconds(maxMs)));

    private static DesktopObservationHint Hint(
        DesktopObservationHintKind kind,
        string source,
        DateTimeOffset at) =>
        new(kind, source, at);
}
