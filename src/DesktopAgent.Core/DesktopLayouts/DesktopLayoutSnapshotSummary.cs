using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts;

/// <summary>
/// 快照摘要，用于列表展示。不含完整图标数据。
/// </summary>
public sealed record DesktopLayoutSnapshotSummary
{
    public Guid SnapshotId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? TargetMonitorDeviceName { get; }
    public ConnectionMethod ConnectionMethod { get; }
    public int IconCount { get; }
    public int FormatVersion { get; }

    public DesktopLayoutSnapshotSummary(
        Guid snapshotId,
        DateTimeOffset createdAtUtc,
        string? targetMonitorDeviceName,
        ConnectionMethod connectionMethod,
        int iconCount,
        int formatVersion)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("SnapshotId 不能为 Guid.Empty。", nameof(snapshotId));
        if (formatVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(formatVersion), "FormatVersion 必须大于 0。");
        if (iconCount < 0)
            throw new ArgumentOutOfRangeException(nameof(iconCount), "IconCount 不能为负数。");

        SnapshotId = snapshotId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        TargetMonitorDeviceName = targetMonitorDeviceName;
        ConnectionMethod = connectionMethod;
        IconCount = iconCount;
        FormatVersion = formatVersion;
    }
}
