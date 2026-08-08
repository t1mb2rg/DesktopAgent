using System.Collections.ObjectModel;

namespace DesktopAgent.Application.DesktopObservations;

public enum DesktopObservationHintKind
{
    Unknown = 0,
    FileSystem = 1,
    Shell = 2,
    DisplayTopology = 3,
    PeriodicFallback = 4,
    Manual = 5,
}

/// <summary>
/// “桌面可能发生变化”的提示。Hint 不是桌面事实，不得携带或替代 DesktopObservation。
/// </summary>
public sealed record DesktopObservationHint
{
    public DesktopObservationHintKind Kind { get; }
    public string Source { get; }
    public DateTimeOffset ReceivedAtUtc { get; }

    public DesktopObservationHint(
        DesktopObservationHintKind kind,
        string source,
        DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Hint source 不能为空。", nameof(source));

        Kind = kind;
        Source = source.Trim();
        ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
    }
}

public sealed record DesktopObservationHintBatchSettings
{
    public TimeSpan QuietPeriod { get; }
    public TimeSpan MaxBatchDelay { get; }

    public DesktopObservationHintBatchSettings(
        TimeSpan quietPeriod,
        TimeSpan maxBatchDelay)
    {
        if (quietPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        if (maxBatchDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxBatchDelay));
        if (maxBatchDelay < quietPeriod)
        {
            throw new ArgumentException(
                "MaxBatchDelay 必须大于或等于 QuietPeriod。",
                nameof(maxBatchDelay));
        }

        QuietPeriod = quietPeriod;
        MaxBatchDelay = maxBatchDelay;
    }
}

public sealed record DesktopObservationHintBatch
{
    public DateTimeOffset FirstHintAtUtc { get; }
    public DateTimeOffset LastHintAtUtc { get; }
    public DateTimeOffset DueAtUtc { get; }
    public int TotalHintCount { get; }
    public IReadOnlyDictionary<DesktopObservationHintKind, int> HintCounts { get; }
    public IReadOnlyList<string> Sources { get; }

    public TimeSpan Span => LastHintAtUtc - FirstHintAtUtc;

    public DesktopObservationHintBatch(
        DateTimeOffset firstHintAtUtc,
        DateTimeOffset lastHintAtUtc,
        DateTimeOffset dueAtUtc,
        int totalHintCount,
        IReadOnlyDictionary<DesktopObservationHintKind, int> hintCounts,
        IReadOnlyList<string> sources)
    {
        if (totalHintCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalHintCount));
        ArgumentNullException.ThrowIfNull(hintCounts);
        ArgumentNullException.ThrowIfNull(sources);

        FirstHintAtUtc = firstHintAtUtc.ToUniversalTime();
        LastHintAtUtc = lastHintAtUtc.ToUniversalTime();
        DueAtUtc = dueAtUtc.ToUniversalTime();
        TotalHintCount = totalHintCount;
        HintCounts = new ReadOnlyDictionary<DesktopObservationHintKind, int>(
            new Dictionary<DesktopObservationHintKind, int>(hintCounts));
        Sources = Array.AsReadOnly(
            sources
                .Distinct(StringComparer.Ordinal)
                .OrderBy(source => source, StringComparer.Ordinal)
                .ToArray());
    }
}

public sealed record DesktopObservationHintRegistration(
    DesktopObservationHintBatch? ReadyBatch,
    DateTimeOffset PendingDueAtUtc);

/// <summary>
/// M3 event hint 的纯状态合并器。
/// QuietPeriod 用于等待事件风暴安静；MaxBatchDelay 防止持续事件让 observation 永久饥饿。
/// 本类不启动线程、不调用 Observation、不调用 Writer。
/// </summary>
public sealed class DesktopObservationHintBatcher
{
    private readonly object _gate = new();
    private readonly DesktopObservationHintBatchSettings _settings;
    private PendingBatch? _pending;

    public DesktopObservationHintBatcher(DesktopObservationHintBatchSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public DateTimeOffset? NextDueAtUtc
    {
        get
        {
            lock (_gate)
                return _pending?.DueAtUtc;
        }
    }

    /// <summary>
    /// 注册一个 hint。若旧 batch 在该 hint 到达前已经到期，则先返回旧 ReadyBatch，
    /// 新 hint 会成为下一 batch 的第一条，避免跨过触发边界继续吞并。
    /// </summary>
    public DesktopObservationHintRegistration RegisterHint(DesktopObservationHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);

        lock (_gate)
        {
            DesktopObservationHintBatch? ready = null;
            var receivedAt = hint.ReceivedAtUtc;

            if (_pending is not null && receivedAt >= _pending.DueAtUtc)
            {
                ready = _pending.ToImmutable();
                _pending = null;
            }

            if (_pending is null)
            {
                _pending = PendingBatch.Start(hint, _settings);
            }
            else
            {
                _pending.Add(hint, _settings);
            }

            return new DesktopObservationHintRegistration(
                ready,
                _pending.DueAtUtc);
        }
    }

    public DesktopObservationHintBatch? TryTakeReady(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_pending is null)
                return null;

            var now = nowUtc.ToUniversalTime();
            if (now < _pending.DueAtUtc)
                return null;

            var ready = _pending.ToImmutable();
            _pending = null;
            return ready;
        }
    }

    /// <summary>
    /// 仅用于显式停止/诊断场景。正常后台调度应等待 DueAtUtc。
    /// </summary>
    public DesktopObservationHintBatch? TakePendingNow()
    {
        lock (_gate)
        {
            if (_pending is null)
                return null;

            var batch = _pending.ToImmutable();
            _pending = null;
            return batch;
        }
    }

    public void Reset()
    {
        lock (_gate)
            _pending = null;
    }

    private sealed class PendingBatch
    {
        private readonly Dictionary<DesktopObservationHintKind, int> _counts = [];
        private readonly HashSet<string> _sources = new(StringComparer.Ordinal);

        public DateTimeOffset FirstHintAtUtc { get; private set; }
        public DateTimeOffset LastHintAtUtc { get; private set; }
        public DateTimeOffset DueAtUtc { get; private set; }
        public int TotalHintCount { get; private set; }

        private PendingBatch()
        {
        }

        public static PendingBatch Start(
            DesktopObservationHint hint,
            DesktopObservationHintBatchSettings settings)
        {
            var batch = new PendingBatch
            {
                FirstHintAtUtc = hint.ReceivedAtUtc,
                LastHintAtUtc = hint.ReceivedAtUtc,
            };
            batch.AddCore(hint);
            batch.RecalculateDue(settings);
            return batch;
        }

        public void Add(
            DesktopObservationHint hint,
            DesktopObservationHintBatchSettings settings)
        {
            // Hint 时间是诊断元数据。系统时钟回拨或来源乱序时不允许 batch 时间倒退。
            var effectiveTime = hint.ReceivedAtUtc < LastHintAtUtc
                ? LastHintAtUtc
                : hint.ReceivedAtUtc;

            LastHintAtUtc = effectiveTime;
            AddCore(hint);
            RecalculateDue(settings);
        }

        private void AddCore(DesktopObservationHint hint)
        {
            TotalHintCount++;
            _counts.TryGetValue(hint.Kind, out var count);
            _counts[hint.Kind] = count + 1;
            _sources.Add(hint.Source);
        }

        private void RecalculateDue(DesktopObservationHintBatchSettings settings)
        {
            var quietDue = LastHintAtUtc + settings.QuietPeriod;
            var maxDue = FirstHintAtUtc + settings.MaxBatchDelay;
            DueAtUtc = quietDue <= maxDue ? quietDue : maxDue;
        }

        public DesktopObservationHintBatch ToImmutable() =>
            new(
                FirstHintAtUtc,
                LastHintAtUtc,
                DueAtUtc,
                TotalHintCount,
                _counts,
                _sources.ToArray());
    }
}
