using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

/// <summary>
/// 可脚本化的 Writer 测试替身。
/// 脚本按 FIFO 顺序消费 — 同一 StableId 的多次调用按注册顺序消费。
/// </summary>
public sealed class ScriptedDesktopIconPositionWriter : IDesktopIconPositionWriter
{
    private readonly DesktopIconPositioningCapabilities _capabilities;
    private readonly Queue<ScriptedMove> _scripts = new();

    public IReadOnlyList<DesktopIconMoveResult> CallHistory { get; private set; } = Array.Empty<DesktopIconMoveResult>();
    public int MoveCallCount => CallHistory.Count;

    public ScriptedDesktopIconPositionWriter(DesktopIconPositioningCapabilities capabilities)
    {
        _capabilities = capabilities;
    }

    public ScriptedDesktopIconPositionWriter()
        : this(new DesktopIconPositioningCapabilities(false, new PixelPoint(80, 80),
            ConnectionMethod.FindWindowSW, null))
    { }

    // ═══ 脚本配置 ═══

    public void ScriptExactResult(string stableId, PixelPoint previousPosition, PixelPoint targetPosition)
    {
        Enqueue(stableId, previousPosition,
            () => new DesktopIconMoveResult(stableId, previousPosition, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null));
    }

    public void ScriptInexactResult(string stableId, PixelPoint previousPosition,
        PixelPoint targetPosition, PixelPoint actualPosition)
    {
        Enqueue(stableId, previousPosition,
            () => new DesktopIconMoveResult(stableId, previousPosition, targetPosition, actualPosition,
                ConnectionMethod.FindWindowSW, null));
    }

    public void ScriptWrongStableId(string stableId, PixelPoint previousPosition,
        PixelPoint targetPosition, string actualStableId)
    {
        Enqueue(stableId, previousPosition,
            () => new DesktopIconMoveResult(actualStableId, previousPosition, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null));
    }

    public void ScriptWrongPreviousPosition(string stableId, PixelPoint expectedPrevious,
        PixelPoint targetPosition, PixelPoint actualPrevious)
    {
        Enqueue(stableId, expectedPrevious,
            () => new DesktopIconMoveResult(stableId, actualPrevious, targetPosition, targetPosition,
                ConnectionMethod.FindWindowSW, null));
    }

    public void ScriptException(string stableId, PixelPoint previousPosition, Exception exception)
    {
        Enqueue(stableId, previousPosition, () => throw exception);
    }

    private void Enqueue(string stableId, PixelPoint previousPosition,
        Func<DesktopIconMoveResult> func)
    {
        _scripts.Enqueue(new ScriptedMove(stableId, previousPosition, func));
    }

    // ═══ Writer 实现 ═══

    public DesktopIconPositioningCapabilities GetCapabilities() => _capabilities;

    public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
    {
        if (_scripts.Count == 0)
            throw new InvalidOperationException(
                $"ScriptedWriter 没有剩余脚本：StableId=\"{stableId}\", Target=({targetPosition.X},{targetPosition.Y})。");

        var match = _scripts.Dequeue();
        if (!string.Equals(match.StableId, stableId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"ScriptedWriter StableId 不匹配：预期 \"{match.StableId}\" 但被调用 \"{stableId}\"。" +
                $" Target=({targetPosition.X},{targetPosition.Y})。");

        var result = match.Func();

        var history = new List<DesktopIconMoveResult>(CallHistory) { result };
        CallHistory = history;

        return result;
    }

    private sealed class ScriptedMove
    {
        public string StableId { get; }
        public PixelPoint PreviousPosition { get; }
        public Func<DesktopIconMoveResult> Func { get; }

        public ScriptedMove(string stableId, PixelPoint previousPosition,
            Func<DesktopIconMoveResult> func)
        {
            StableId = stableId;
            PreviousPosition = previousPosition;
            Func = func;
        }
    }
}
