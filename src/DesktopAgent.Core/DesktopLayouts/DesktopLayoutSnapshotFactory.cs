using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts;

/// <summary>
/// 将读取结果和显示元数据转换为持久化快照。
/// Factory 内部生成 SnapshotId 和时间戳，调用方不需要提供。
/// </summary>
public sealed class DesktopLayoutSnapshotFactory
{
    private readonly TimeProvider _timeProvider;

    public DesktopLayoutSnapshotFactory(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 从图标描述列表创建快照。
    /// Factory 内部生成 Guid、UTC 时间戳和连续 Ordinal。
    /// </summary>
    public DesktopLayoutSnapshot Create(
        IReadOnlyList<DesktopIconDescriptor> icons,
        string? targetMonitorDeviceName,
        ConnectionMethod connectionMethod)
    {
        var items = new List<DesktopIconSnapshotItem>(icons.Count);
        for (var i = 0; i < icons.Count; i++)
        {
            var d = icons[i];
            items.Add(new DesktopIconSnapshotItem(
                ordinal: i,
                stableId: d.StableId,
                displayName: d.DisplayName,
                parsingName: d.ParsingName,
                itemKind: d.ItemKind,
                pixelPosition: d.PixelPosition,
                monitorDeviceName: d.MonitorDeviceName,
                isSystemItem: d.IsSystemItem,
                isFileSystemItem: d.IsFileSystemItem));
        }

        return new DesktopLayoutSnapshot(
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            targetMonitorDeviceName,
            connectionMethod,
            DesktopLayoutSnapshot.CurrentFormatVersion,
            items);
    }
}
