using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Poc.Acceptance;

public sealed class AcceptanceInjectedSoftFaultException : Exception
{
    public AcceptanceInjectedSoftFaultException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// M2 实机验收专用 Writer decorator。
/// 当且仅当指定的 1-based Move 调用、StableId 与 Exact 写入同时命中时，
/// 在真实 Writer 已完成物理移动、调用方尚未获得返回值之前注入故障。
/// HardCrash 用于精确复现 crash window；SoftError 用于日常 PoC 调试，不终止进程。
/// </summary>
public sealed class AcceptanceFaultInjectingDesktopIconPositionWriter : IDesktopIconPositionWriter
{
    private readonly IDesktopIconPositionWriter _inner;
    private readonly AcceptanceFaultInjectionOptions _options;
    private readonly Action<string> _terminateProcess;
    private readonly Action<string>? _scheduleSoftFaultUiReturn;
    private int _moveCallCount;

    public AcceptanceFaultInjectingDesktopIconPositionWriter(
        IDesktopIconPositionWriter inner,
        AcceptanceFaultInjectionOptions options,
        Action<string>? terminateProcess = null,
        Action<string>? scheduleSoftFaultUiReturn = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _terminateProcess = terminateProcess ?? Environment.FailFast;
        _scheduleSoftFaultUiReturn = scheduleSoftFaultUiReturn;
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
                "受控故障注入命中了调用序号，但 Writer 返回了不同 StableId；拒绝注入故障。"
            );
        }

        if (!result.IsExact)
        {
            throw new InvalidOperationException(
                "受控故障注入命中了调用序号和 StableId，但真实 Writer 未报告 ExactSuccess；拒绝注入故障。"
            );
        }

        var message =
            "DesktopAgent M2 acceptance fault injection triggered after exact physical write " +
            $"and before caller checkpoint. Behavior={_options.Behavior}; " +
            $"MoveCall={callIndex}; StableId={stableId}; " +
            $"Requested={result.RequestedPosition}; Actual={result.ActualPosition}.";

        if (_options.Behavior == AcceptanceFaultInjectionBehavior.SoftError)
        {
            // 先安排 UI 在当前执行异常完成 fail-closed 收口后返回主界面。
            // 调度器本身不能承担安全判断；真正的人工处理锁仍由现有执行 UI / Coordinator 路径建立。
            _scheduleSoftFaultUiReturn?.Invoke(message);
            throw new AcceptanceInjectedSoftFaultException(message);
        }

        _terminateProcess(message);

        // Environment.FailFast 不会返回。该异常只用于单元测试或防止替代终止器意外返回后继续执行。
        throw new InvalidOperationException(
            "受控故障注入终止器意外返回；为避免把该写入误当作正常完成，拒绝继续执行。"
        );
    }
}
