using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts;

/// <summary>
/// 桌面布局只读快照。构造时执行完整验证并防御性复制 Icons。
/// </summary>
public sealed record DesktopLayoutSnapshot
{
    public Guid SnapshotId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? TargetMonitorDeviceName { get; }
    public ConnectionMethod ConnectionMethod { get; }
    public int FormatVersion { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> Icons { get; }

    public const int CurrentFormatVersion = 1;

    public DesktopLayoutSnapshot(
        Guid snapshotId,
        DateTimeOffset createdAtUtc,
        string? targetMonitorDeviceName,
        ConnectionMethod connectionMethod,
        int formatVersion,
        IEnumerable<DesktopIconSnapshotItem> icons)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("SnapshotId 不能为 Guid.Empty。", nameof(snapshotId));
        if (formatVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(formatVersion), "FormatVersion 必须大于 0。");
        if (icons is null)
            throw new ArgumentNullException(nameof(icons));

        SnapshotId = snapshotId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        TargetMonitorDeviceName = targetMonitorDeviceName;
        ConnectionMethod = connectionMethod;
        FormatVersion = formatVersion;

        var list = icons.ToList();
        var ordinals = new HashSet<int>();
        foreach (var item in list)
        {
            if (item.Ordinal < 0)
                throw new ArgumentException($"图标 Ordinal 不能为负数: {item.Ordinal}");
            if (!ordinals.Add(item.Ordinal))
                throw new ArgumentException($"快照内 Ordinal 重复: {item.Ordinal}");
        }
        Icons = list.AsReadOnly();
    }
}
