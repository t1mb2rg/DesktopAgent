using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Poc.Acceptance;

/// <summary>
/// M2 实机验收专用 Writer decorator。
/// 当且仅当指定的 1-based Move 调用、StableId 与 Exact 写入同时命中时，
/// 在真实 Writer 已完成物理移动、调用方尚未获得返回值之前终止进程。
/// 这用于稳定复现“物理副作用已发生，但 checkpoint 尚未持久化”的 crash window。
/// </summary>
public sealed class AcceptanceFaultInjectingDesktopIconPositionWriter : IDesktopIconPositionWriter
{
    private readonly IDesktopIconPositionWriter _inner;
    private readonly AcceptanceFaultInjectionOptions _options;
    private readonly Action<string> _terminateProcess;
    private int _moveCallCount;

    public AcceptanceFaultInjectingDesktopIconPositionWriter(
        IDesktopIconPositionWriter inner,
        AcceptanceFaultInjectionOptions options,
        Action<string>? terminateProcess = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _terminateProcess = terminateProcess ?? Environment.FailFast;
    }

    public DesktopIconPositioningCapabilities GetCapabilities() =>
        _inner.GetCapabilities();

    public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
    {
        var callIndex = Interlocked.Increment(ref _moveCallCount);
        var result = _inner.MoveDesktopIcon(stableId, targetPosition);

        if (!_options.IsEnabled
            || callIndex != _options.MoveCallIndex
            || !string.Equals(stableId, _options.StableId, StringComparison.Ordinal))
        {
            return result;
        }

        if (!string.Equals(result.StableId, _options.StableId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "受控故障注入命中了调用序号，但 Writer 返回了不同 StableId；拒绝终止进程。"
            );
        }

        if (!result.IsExact)
        {
            throw new InvalidOperationException(
                "受控故障注入命中了调用序号和 StableId，但真实 Writer 未报告 ExactSuccess；拒绝终止进程。"
            );
        }

        var message =
            "DesktopAgent M2 acceptance fault injection triggered after exact physical write " +
            $"and before caller checkpoint. MoveCall={callIndex}; StableId={stableId}; " +
            $"Requested={result.RequestedPosition}; Actual={result.ActualPosition}.";

        _terminateProcess(message);

        // Environment.FailFast 不会返回。该异常只用于单元测试或防止替代终止器意外返回后继续执行。
        throw new InvalidOperationException(
            "受控故障注入终止器意外返回；为避免把该写入误当作正常完成，拒绝继续执行。"
        );
    }
}
