using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 「现在为什么不能撤销上次整理」的唯一枚举。
/// </summary>
public enum DesktopLayoutRollbackBlockReason
{
    /// <summary>可以撤销。</summary>
    None = 0,

    /// <summary>还没有任何一次成功收尾的整理。</summary>
    NoCommittedSession = 1,

    /// <summary>最近一次整理没有正常收尾（现场还没收敛）。</summary>
    SessionNotCompleted = 2,

    /// <summary>上次整理没有移动任何图标。</summary>
    NothingWasMoved = 3,

    /// <summary>找不到那次整理之前的安全检查点。</summary>
    SnapshotMissing = 4,

    /// <summary>安全检查点不是这块屏的（不能拿来撤销这块屏）。</summary>
    SnapshotNotForSameDisplay = 5,

    /// <summary>当前拓扑里已经没有那块目标屏。</summary>
    TargetDisplayMissing = 6,

    /// <summary>桌面自上次整理之后被改过，盲目回退会把用户的改动一起带走。</summary>
    DesktopChangedSinceApply = 7,

    /// <summary>核对通过，但按快照位置没有需要移动的项目。</summary>
    NothingToUndo = 8,

    /// <summary>读不到当前桌面，撤销入口保持关闭。</summary>
    CurrentDesktopUnreadable = 9,

    /// <summary>有未收敛的执行现场，撤销要先让现场收敛。</summary>
    UnresolvedRecoverySite = 10,

    /// <summary>按安全检查点排不出可执行的还原计划（被别的图标占住、互相挡路或项目已不在）。</summary>
    RestorePlanBlocked = 11,

    /// <summary>执行安全门不放行这次还原。</summary>
    ScopeNotAllowed = 12,
}

/// <summary>
/// 撤销前置判定的结论：能不能撤销、为什么、要动几个项目、哪些项目已经对不上。
/// 纯数据，不含 COM/坐标空间/持久化类型。
/// </summary>
public sealed record DesktopLayoutRollbackPreflightResult(
    bool CanRollback,
    DesktopLayoutRollbackBlockReason Reason,
    string Headline,
    string Detail,
    Guid SessionId,
    Guid SafetySnapshotId,
    string TargetDeviceName,
    int MoveBackCount,
    IReadOnlyList<string> ChangedItemNames)
{
    public static DesktopLayoutRollbackPreflightResult Blocked(
        DesktopLayoutRollbackBlockReason reason,
        string headline,
        string detail,
        RecoverySessionRecord? session = null,
        IReadOnlyList<string>? changedItemNames = null)
        => ForCandidate(
            reason,
            headline,
            detail,
            DesktopLayoutRollbackCandidate.TryFromRecoverySession(session),
            changedItemNames);

    public static DesktopLayoutRollbackPreflightResult ForCandidate(
        DesktopLayoutRollbackBlockReason reason,
        string headline,
        string detail,
        DesktopLayoutRollbackCandidate? candidate,
        IReadOnlyList<string>? changedItemNames = null)
        => new(
            false,
            reason,
            headline,
            detail,
            candidate?.SessionId ?? Guid.Empty,
            candidate?.SafetySnapshotId ?? Guid.Empty,
            candidate?.TargetDeviceName ?? string.Empty,
            0,
            changedItemNames is null ? Array.Empty<string>() : Array.AsReadOnly(changedItemNames.ToArray()));
}

/// <summary>
/// 「撤销上次整理」的只读前置判定。
///
/// 撤销的语义是<strong>回到那次整理之前</strong>：目标位置来自那次写盘前的安全检查点快照，
/// 而不是「把每个项目反向挪一次」。所以判定要回答两件事：
/// ① 有没有一份可信的「原来长什么样」（已收尾的会话/工作区写入 + 同一份安全检查点）；
/// ② 桌面现在是否还是我们上次整理完的样子——若用户之后自己动过东西，
/// 盲目回退会连他的改动一起带走，这里必须拒绝并点名。
///
/// 判定不关心这次整理是从哪条路径写下去的：两条路径都会留下「写过的项目 + 它的终点」，
/// 见 <see cref="DesktopLayoutRollbackCandidate"/>。
/// </summary>
public static class DesktopLayoutRollbackPreflight
{
    /// <summary>最多点名几个「已经被改过」的项目，避免界面被一长串名字淹没。</summary>
    public const int MaxNamedChangedItems = 5;

    /// <summary>
    /// 直接应用（恢复会话）路径的判定：先做会话自身的前置检查，再交给路径无关的判定。
    /// </summary>
    public static DesktopLayoutRollbackPreflightResult Evaluate(
        RecoverySessionRecord? session,
        DesktopLayoutSnapshot? snapshot,
        IReadOnlyList<DesktopIconDescriptor> currentIcons,
        IReadOnlyList<DisplayDescriptor> currentDisplays)
    {
        if (session is null)
        {
            return DesktopLayoutRollbackPreflightResult.Blocked(
                DesktopLayoutRollbackBlockReason.NoCommittedSession,
                "还没有可以撤销的整理",
                "只有在「应用到桌面」成功收尾之后，才留得下一份可回到的原样。");
        }

        if (session.Lifecycle != RecoverySessionLifecycle.Completed)
        {
            return DesktopLayoutRollbackPreflightResult.Blocked(
                DesktopLayoutRollbackBlockReason.SessionNotCompleted,
                "上一轮整理没有正常收尾 · 先处理恢复现场",
                "这次执行还停在可恢复状态里，撤销入口要等它收敛。点「处理恢复状态」把它处理完再回来。",
                session);
        }

        return EvaluateCandidate(
            DesktopLayoutRollbackCandidate.TryFromRecoverySession(session),
            snapshot,
            currentIcons,
            currentDisplays);
    }

    /// <summary>
    /// 路径无关的判定：候选给了「写过的项目 + 每个项目的终点」，快照给了「原来在哪」。
    /// </summary>
    public static DesktopLayoutRollbackPreflightResult EvaluateCandidate(
        DesktopLayoutRollbackCandidate? candidate,
        DesktopLayoutSnapshot? snapshot,
        IReadOnlyList<DesktopIconDescriptor> currentIcons,
        IReadOnlyList<DisplayDescriptor> currentDisplays)
    {
        ArgumentNullException.ThrowIfNull(currentIcons);
        ArgumentNullException.ThrowIfNull(currentDisplays);

        if (candidate is null)
        {
            return DesktopLayoutRollbackPreflightResult.ForCandidate(
                DesktopLayoutRollbackBlockReason.NoCommittedSession,
                "还没有可以撤销的整理",
                "只有在整理成功收尾之后，才留得下一份可回到的原样。",
                candidate: null);
        }

        if (candidate.WrittenItemCount == 0)
        {
            return DesktopLayoutRollbackPreflightResult.ForCandidate(
                DesktopLayoutRollbackBlockReason.NothingWasMoved,
                "上次整理没有移动任何图标",
                "没有动作需要撤销。",
                candidate);
        }

        if (snapshot is null)
        {
            return DesktopLayoutRollbackPreflightResult.ForCandidate(
                DesktopLayoutRollbackBlockReason.SnapshotMissing,
                "找不到那次整理之前的安全检查点",
                "没有检查点就不提供撤销——宁可不动，也不凭推断摆回去。",
                candidate);
        }

        if (candidate.HasTargetDisplay)
        {
            if (!string.Equals(
                    snapshot.TargetMonitorDeviceName,
                    candidate.TargetDeviceName,
                    StringComparison.Ordinal))
            {
                return DesktopLayoutRollbackPreflightResult.ForCandidate(
                    DesktopLayoutRollbackBlockReason.SnapshotNotForSameDisplay,
                    "安全检查点不是这块屏的",
                    $"检查点属于 {snapshot.TargetMonitorDeviceName ?? "（未标注）"}，"
                    + $"而上次整理的是 {candidate.TargetDeviceName}。",
                    candidate);
            }

            if (!currentDisplays.Any(display =>
                    string.Equals(display.DeviceName, candidate.TargetDeviceName, StringComparison.Ordinal)))
            {
                return DesktopLayoutRollbackPreflightResult.ForCandidate(
                    DesktopLayoutRollbackBlockReason.TargetDisplayMissing,
                    "当前拓扑里没有那块目标屏",
                    $"{candidate.TargetDeviceName} 现在不在桌面上，撤销无从落点。",
                    candidate);
            }
        }

        var currentById = new Dictionary<string, DesktopIconDescriptor>(StringComparer.Ordinal);
        foreach (var icon in currentIcons)
            currentById[icon.StableId] = icon;

        var snapshotPositions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        foreach (var item in snapshot.Icons)
            snapshotPositions[item.StableId] = item.PixelPosition;

        var changed = new List<string>();
        var moveBackCount = 0;

        foreach (var written in candidate.WrittenItems)
        {
            var name = written.DisplayName;

            if (!currentById.TryGetValue(written.StableId, out var icon)
                || icon.PixelPosition != written.ExpectedFinalPosition
                || (candidate.HasTargetDisplay
                    && !string.Equals(icon.MonitorDeviceName, candidate.TargetDeviceName, StringComparison.Ordinal)))
            {
                changed.Add(name);
                continue;
            }

            if (snapshotPositions.TryGetValue(written.StableId, out var originalPosition)
                && originalPosition != written.ExpectedFinalPosition)
            {
                moveBackCount++;
            }
        }

        // 这一次整理已经被整块还原过了：每个写过的项目都回到了检查点位置。
        // 此时不该再顺着候选链去撤销别的写盘——真机演练里正是这个状态让第二次「撤销」
        // 把桌面重新整理了一遍。判定放在「桌面被改过」之前，因为这里的「被改过」其实是
        // 用户自己按检查点还原的结果。
        if (candidate.WrittenItemCount > 0)
        {
            var revertedCount = 0;
            foreach (var written in candidate.WrittenItems)
            {
                if (currentById.TryGetValue(written.StableId, out var live)
                    && snapshotPositions.TryGetValue(written.StableId, out var originalPosition)
                    && live.PixelPosition == originalPosition)
                {
                    revertedCount++;
                }
            }

            if (revertedCount == candidate.WrittenItemCount)
            {
                return DesktopLayoutRollbackPreflightResult.ForCandidate(
                    DesktopLayoutRollbackBlockReason.NothingToUndo,
                    "已经回到那次整理之前",
                    $"上次整理写过的 {candidate.WrittenItemCount} 个项目现在都在安全检查点位置上，"
                    + "没有需要再撤一次的东西。",
                    candidate);
            }
        }

        if (changed.Count > 0)
        {
            var named = string.Join("、", changed.Take(MaxNamedChangedItems));
            var suffix = changed.Count > MaxNamedChangedItems
                ? $" 等 {changed.Count} 个项目"
                : string.Empty;
            return DesktopLayoutRollbackPreflightResult.ForCandidate(
                DesktopLayoutRollbackBlockReason.DesktopChangedSinceApply,
                "桌面已经被改过 · 不盲目回退",
                $"{named}{suffix}现在不在上次整理后的位置上。"
                + "撤销会把它们一起带回整理前，可能连带抹掉你后来的调整，所以这里停住。"
                + "想重新整理就再点一次「↻ 预览」并应用。",
                candidate,
                changed);
        }

        if (moveBackCount == 0)
        {
            return DesktopLayoutRollbackPreflightResult.ForCandidate(
                DesktopLayoutRollbackBlockReason.NothingToUndo,
                "现在就是整理前的样子",
                "按安全检查点比对，没有需要挪回去的项目。",
                candidate);
        }

        return new DesktopLayoutRollbackPreflightResult(
            true,
            DesktopLayoutRollbackBlockReason.None,
            "可以撤销上次整理",
            DescribeApprove(candidate, moveBackCount, snapshot),
            candidate.SessionId,
            candidate.SafetySnapshotId,
            candidate.TargetDeviceName ?? string.Empty,
            moveBackCount,
            Array.Empty<string>());
    }

    private static string DescribeApprove(
        DesktopLayoutRollbackCandidate candidate,
        int moveBackCount,
        DesktopLayoutSnapshot snapshot)
    {
        var destinations = ResolveMoveBackDestinations(candidate, snapshot);
        var where = destinations.Count switch
        {
            1 => $"放回 {destinations[0]} 上整理前的位置",
            > 1 => $"放回这次整理前的位置（涉及 {destinations.Count} 块屏："
                + $"{string.Join("、", destinations)}）",
            _ => "放回这次整理前的位置",
        };
        var source = candidate.SourceKind == DesktopLayoutRollbackSourceKind.WorkspaceApply
            ? "；来源：工作区设计应用"
            : string.Empty;

        return $"会把 {moveBackCount} 个项目{where}"
            + $"（回到安全检查点 {candidate.SafetySnapshotId:D}，"
            + $"取自 {snapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{source}）。";
    }

    /// <summary>
    /// 撤销的落点 = 要挪回去的每个项目在安全检查点里所落的屏。
    /// 候选自己的目标屏只能说明「那次整理把图标写到了哪」，不能用来描述撤销会把人放到哪：
    /// 跨屏撤销、以及「撤销一次撤销」时，两者并不相同。
    /// </summary>
    private static IReadOnlyList<string> ResolveMoveBackDestinations(
        DesktopLayoutRollbackCandidate candidate,
        DesktopLayoutSnapshot snapshot)
    {
        var checkpoint = new Dictionary<string, (PixelPoint Position, string? DeviceName)>(StringComparer.Ordinal);
        foreach (var item in snapshot.Icons)
            checkpoint[item.StableId] = (item.PixelPosition, item.MonitorDeviceName);

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var written in candidate.WrittenItems)
        {
            if (!checkpoint.TryGetValue(written.StableId, out var original)
                || original.Position == written.ExpectedFinalPosition)
            {
                continue;
            }

            var device = string.IsNullOrWhiteSpace(original.DeviceName)
                ? candidate.TargetDeviceName
                : original.DeviceName;
            if (!string.IsNullOrWhiteSpace(device) && seen.Add(device!))
                ordered.Add(device!);
        }

        if (candidate.HasTargetDisplay)
        {
            var index = ordered.FindIndex(device =>
                string.Equals(device, candidate.TargetDeviceName, StringComparison.Ordinal));
            if (index > 0)
            {
                var target = ordered[index];
                ordered.RemoveAt(index);
                ordered.Insert(0, target);
            }
        }

        return ordered;
    }
}
