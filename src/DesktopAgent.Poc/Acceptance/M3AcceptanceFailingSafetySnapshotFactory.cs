using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Poc.Acceptance;

/// <summary>
/// M3-B07-D Debug acceptance 专用的 pre-writer failure seam。
///
/// TargetDisplayLayoutExecutionCoordinator 会在 scope gate 已通过后、创建 safety snapshot 时
/// 调用本工厂；因此这里抛出的异常会被现有 Coordinator 结构化为 FailedBeforeWrite，
/// 不会建立 recovery checkpoint，也不会触达 IDesktopIconPositionWriter。
/// </summary>
public sealed class M3AcceptanceFailingSafetySnapshotFactory : IDesktopLayoutSafetySnapshotFactory
{
    public const string InjectedFailureMessage =
        "M3-B07-D injected pre-writer failure at safety snapshot creation.";

    public DesktopLayoutSnapshot Create(
        IReadOnlyList<DesktopIconDescriptor> icons,
        string? targetMonitorDeviceName,
        ConnectionMethod connectionMethod)
    {
        _ = icons ?? throw new ArgumentNullException(nameof(icons));
        _ = targetMonitorDeviceName;
        _ = connectionMethod;

        throw new InvalidOperationException(InjectedFailureMessage);
    }
}
