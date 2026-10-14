using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 「撤销上次整理」要还原哪些显示器，以及为什么某些项目还原不了。
/// 纯数据：显示器设备名并集 + 两类无法归位的项目。
/// </summary>
public sealed record DesktopLayoutRollbackRestoreTargets(
    IReadOnlyList<string> DeviceNames,
    IReadOnlyList<string> ItemsMissingFromSnapshot,
    IReadOnlyList<string> ItemsWithoutDisplay)
{
    /// <summary>这次撤销要写几块屏（一般为 1；跨屏整理过后大于 1）。</summary>
    public int DisplayCount => DeviceNames.Count;

    public bool IsCrossDisplay => DeviceNames.Count > 1;
}

/// <summary>
/// 把「这次整理真正写过的项目」映射成「必须还原的显示器」。
///
/// 为什么不是只用会话的目标屏：跨屏整理（直接应用把别的屏的项目挪进目标屏，
/// 或工作区设计把三块屏的项目汇到一块屏）之后，这些项目在安全检查点里的归属
/// 仍在它们原来的屏上。只还原目标屏会留下它们不动，撤销就不完整
/// （桌面回不到整理前）。这里对每个写过的项目取其在快照里的
/// <c>MonitorDeviceName</c> 求并集，正好等于「被这次写入动过的屏」。
///
/// 顺序固定：目标屏优先（有目标屏时），其余按键名序——同一现场每次都给出同一个计划顺序。
/// </summary>
public static class DesktopLayoutRollbackRestoreTargetResolver
{
    /// <summary>直接应用（恢复会话）路径：写过的项目 = 已完成的前向步骤。</summary>
    public static DesktopLayoutRollbackRestoreTargets Resolve(
        RecoverySessionRecord session,
        DesktopLayoutSnapshot snapshot)
        => Resolve(DesktopLayoutRollbackCandidate.TryFromRecoverySession(session), snapshot);

    /// <summary>路径无关：写过的项目由候选给出，快照给出它们原来在哪块屏。</summary>
    public static DesktopLayoutRollbackRestoreTargets Resolve(
        DesktopLayoutRollbackCandidate? candidate,
        DesktopLayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var byStableId = new Dictionary<string, DesktopIconSnapshotItem>(StringComparer.Ordinal);
        foreach (var icon in snapshot.Icons)
            byStableId.TryAdd(icon.StableId, icon);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var withoutDisplay = new List<string>();

        foreach (var written in candidate?.WrittenItems ?? Array.Empty<DesktopLayoutRollbackWrittenItem>())
        {
            if (!byStableId.TryGetValue(written.StableId, out var icon))
            {
                missing.Add(written.StableId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(icon.MonitorDeviceName))
            {
                withoutDisplay.Add(written.StableId);
                continue;
            }

            if (seen.Add(icon.MonitorDeviceName!))
                names.Add(icon.MonitorDeviceName!);
        }

        names.Sort(StringComparer.Ordinal);
        if (candidate is not null)
        {
            var primaryIndex = names.FindIndex(name =>
                string.Equals(name, candidate.TargetDeviceName, StringComparison.Ordinal));
            if (primaryIndex > 0)
            {
                var primary = names[primaryIndex];
                names.RemoveAt(primaryIndex);
                names.Insert(0, primary);
            }
        }

        return new DesktopLayoutRollbackRestoreTargets(
            Array.AsReadOnly(names.ToArray()),
            Array.AsReadOnly(missing.ToArray()),
            Array.AsReadOnly(withoutDisplay.ToArray()));
    }
}
