using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class ScriptedDesktopIconPositionWriter : IDesktopIconPositionWriter
{
    private readonly DesktopIconPositioningCapabilities _capabilities;
    private readonly Queue<ScriptedMove> _scripts = new();

    /// <summary>每次 MoveDesktopIcon 调用的记录（包括抛异常的调用）。</summary>
    public IReadOnlyList<AttemptRecord> AttemptHistory { get; private set; } = Array.Empty<AttemptRecord>();

    /// <summary>成功返回的结果历史。</summary>
    public IReadOnlyList<DesktopIconMoveResult> CallHistory { get; private set; } = Array.Empty<DesktopIconMoveResult>();

    /// <summary>所有调用次数（包括异常）。</summary>
    public int AttemptCount => AttemptHistory.Count;

    /// <summary>成功调用次数。</summary>
    public int MoveCallCount => CallHistory.Count;

    public ScriptedDesktopIconPositionWriter(DesktopIconPositioningCapabilities capabilities)
    { _capabilities = capabilities; }

    public ScriptedDesktopIconPositionWriter()
        : this(new DesktopIconPositioningCapabilities(false, new PixelPoint(80, 80),
            ConnectionMethod.FindWindowSW, null)) { }

    /// <summary>单次调用记录。</summary>
    public sealed record AttemptRecord(string StableId, PixelPoint TargetPosition, int AttemptIndex,
        bool DidThrow, string? ExceptionType);

    // ═══ 脚本配置 ═══

    public void ScriptExactResult(string stableId, PixelPoint expectedPrevious, PixelPoint targetPosition) =>
        Enqueue(stableId, targetPosition,
            () => new DesktopIconMoveResult(stableId, expectedPrevious, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null));

    public void ScriptInexactResult(string stableId, PixelPoint expectedPrevious,
        PixelPoint targetPosition, PixelPoint actualPosition) =>
        Enqueue(stableId, targetPosition,
            () => new DesktopIconMoveResult(stableId, expectedPrevious, targetPosition, actualPosition,
                ConnectionMethod.FindWindowSW, null));

    public void ScriptWrongStableId(string stableId, PixelPoint expectedPrevious,
        PixelPoint targetPosition, string actualStableId) =>
        Enqueue(stableId, targetPosition,
            () => new DesktopIconMoveResult(actualStableId, expectedPrevious, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null));

    public void ScriptWrongPreviousPosition(string stableId, PixelPoint expectedPrevious,
        PixelPoint targetPosition, PixelPoint actualPrevious) =>
        Enqueue(stableId, targetPosition,
            () => new DesktopIconMoveResult(stableId, actualPrevious, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null));

    public void ScriptException(string stableId, PixelPoint targetPosition, Exception exception) =>
        Enqueue(stableId, targetPosition, () => throw exception);

    /// <summary>
    /// 阻塞脚本：Writer 调用开始时设置 started 信号，然后等待 release 信号再返回结果。
    /// </summary>
    public void ScriptBlocking(string stableId, PixelPoint targetPosition, PixelPoint previousPosition,
        TaskCompletionSource startedSignal, TaskCompletionSource releaseSignal)
    {
        Enqueue(stableId, targetPosition, () =>
        {
            startedSignal.TrySetResult();
            releaseSignal.Task.GetAwaiter().GetResult();
            return new DesktopIconMoveResult(stableId, previousPosition, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null);
        });
    }

    /// <summary>
    /// 阻塞+精确结果：Writer 调用开始时设置 started 信号，然后等待 release 信号再返回精确结果。
    /// </summary>
    public void ScriptBlockingExact(string stableId, PixelPoint targetPosition, PixelPoint previousPosition,
        TaskCompletionSource startedSignal, TaskCompletionSource releaseSignal)
    {
        Enqueue(stableId, targetPosition, () =>
        {
            startedSignal.TrySetResult();
            releaseSignal.Task.GetAwaiter().GetResult();
            return new DesktopIconMoveResult(stableId, previousPosition, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null);
        });
    }

    private void Enqueue(string stableId, PixelPoint targetPosition, Func<DesktopIconMoveResult> func) =>
        _scripts.Enqueue(new ScriptedMove(stableId, targetPosition, func));

    // ═══ Writer 实现 ═══

    public DesktopIconPositioningCapabilities GetCapabilities() => _capabilities;

    public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
    {
        if (_scripts.Count == 0)
            throw new InvalidOperationException(
                $"ScriptedWriter 没有剩余脚本：StableId=\"{stableId}\", Target=({targetPosition.X},{targetPosition.Y})。");

        var match = _scripts.Dequeue();

        // 验证 StableId
        if (!string.Equals(match.ExpectedStableId, stableId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"ScriptedWriter StableId 不匹配：预期 \"{match.ExpectedStableId}\" 但被调用 \"{stableId}\"。" +
                $" Target=({targetPosition.X},{targetPosition.Y})。");

        // 验证 TargetPosition
        if (match.ExpectedTargetPosition != targetPosition)
            throw new InvalidOperationException(
                $"ScriptedWriter TargetPosition 不匹配：预期 ({match.ExpectedTargetPosition.X},{match.ExpectedTargetPosition.Y}) " +
                $"但被调用 ({targetPosition.X},{targetPosition.Y})，StableId=\"{stableId}\"。");

        var attemptIdx = AttemptHistory.Count;
        DesktopIconMoveResult result;

        try
        {
            result = match.Func();
        }
        catch (Exception ex)
        {
            // 记录失败尝试
            var hist = new List<AttemptRecord>(AttemptHistory)
            {
                new(stableId, targetPosition, attemptIdx, true, ex.GetType().Name)
            };
            AttemptHistory = hist;
            throw;
        }

        // 记录成功尝试
        var successHist = new List<AttemptRecord>(AttemptHistory)
        {
            new(stableId, targetPosition, attemptIdx, false, null)
        };
        AttemptHistory = successHist;

        var callHist = new List<DesktopIconMoveResult>(CallHistory) { result };
        CallHistory = callHist;

        return result;
    }

    private sealed class ScriptedMove
    {
        public string ExpectedStableId { get; }
        public PixelPoint ExpectedTargetPosition { get; }
        public Func<DesktopIconMoveResult> Func { get; }

        public ScriptedMove(string expectedStableId, PixelPoint expectedTargetPosition,
            Func<DesktopIconMoveResult> func)
        {
            ExpectedStableId = expectedStableId;
            ExpectedTargetPosition = expectedTargetPosition;
            Func = func;
        }
    }
}
