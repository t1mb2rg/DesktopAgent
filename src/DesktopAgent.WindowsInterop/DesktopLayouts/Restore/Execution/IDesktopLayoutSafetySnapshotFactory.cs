using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// 安全快照工厂抽象。生产实现委托 DesktopLayoutSnapshotFactory。
/// </summary>
public interface IDesktopLayoutSafetySnapshotFactory
{
    DesktopLayoutSnapshot Create(
        IReadOnlyList<DesktopIconDescriptor> icons,
        string? targetMonitorDeviceName,
        ConnectionMethod connectionMethod);
}

/// <summary>生产适配器。</summary>
public sealed class DefaultSafetySnapshotFactory : IDesktopLayoutSafetySnapshotFactory
{
    private readonly DesktopLayoutSnapshotFactory _inner;

    public DefaultSafetySnapshotFactory(DesktopLayoutSnapshotFactory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public DesktopLayoutSnapshot Create(
        IReadOnlyList<DesktopIconDescriptor> icons,
        string? targetMonitorDeviceName,
        ConnectionMethod connectionMethod) =>
        _inner.Create(icons, targetMonitorDeviceName, connectionMethod);
}
