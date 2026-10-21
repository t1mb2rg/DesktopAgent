using System.Linq;
using System.Text;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

public enum ExplorerGridProbeRunStatus
{
    Passed = 0,
    Failed = 1,
    Cancelled = 2,
    Blocked = 3,

    /// <summary>
    /// 部分显示器测到了：至少一块屏拿到了合格事实，另有屏明确测不到（例如 Explorer
    /// 不把探针快捷方式留在该屏）。事实逐屏写：测到的屏写入 Measured 事实，未测到的屏
    /// 只记录原因，由策略层拒绝在该屏上执行。
    /// </summary>
    Partial = 4,
}

public enum ExplorerGridProbeMoveKind
{
    Seed = 0,
    Horizontal = 1,
    Vertical = 2,
    Corner = 3,
    Restore = 4,
}

public sealed record ExplorerGridProbeShortcut(string FilePath);

public sealed record ExplorerGridProbeMove(
    string DisplayDeviceName,
    string ShortcutStableId,
    ExplorerGridProbeMoveKind Kind,
    int Ordinal,
    PixelPoint Previous,
    PixelPoint Requested,
    PixelPoint Observed,
    bool IsExact)
{
    /// <summary>
    /// Writer 的 Observed 是同一 IFolderView 连接内立即读回；Explorer 仍可能随后异步吸附。
    /// SettledObserved 由双采样 Stable Observation 确认。
    /// </summary>
    public PixelPoint? SettledObserved { get; init; }
    public bool? SettledIsExact { get; init; }
}

public sealed record ExplorerGridProbePassiveSample(
    string DisplayDeviceName,
    ExplorerGridProbeMoveKind Kind,
    int Ordinal,
    PixelPoint Observed,
    string OccupantStableId);

public sealed record ExplorerGridProbeDisplayResult(
    string DeviceName,
    PixelPoint Spacing,
    PixelPoint Phase,
    int HorizontalSampleCount,
    int VerticalSampleCount,
    int CornerSampleCount,
    bool ObservedSpacingConstant,
    bool ObservedPhaseConstant)
{
    public int ActiveScanSampleCount { get; init; }
    public int PassiveOccupiedSampleCount { get; init; }

    /// <summary>
    /// 每轴的主动落点样本数。Physical Gate 要求每轴至少 3 个；
    /// 被动占用样本只作为佐证，不计入 Measured 事实的样本数。
    /// </summary>
    public int ActiveHorizontalSampleCount { get; init; }
    public int ActiveVerticalSampleCount { get; init; }
    public int ActiveCornerSampleCount { get; init; }

    /// <summary>
    /// 落点偏离计划槽位（Explorer 异步回写自己保存的布局）而被跳过、不计入样本的落点数量。
    /// 只要偏离后的位置仍在同一晶格上就没有安全影响，但必须在报告里可见。
    /// </summary>
    public int SkippedLandingCount { get; init; }

    /// <summary>
    /// 该显示器既有桌面图标晶格的相位众数（只读旁证，与 seed 落点无关）。
    /// 相位是"每块屏各自工作区原点"的属性，因此不能跨显示器共享：
    /// 三块屏的任务栏位置不同时，绝对相位本来就不同。
    /// </summary>
    public PixelPoint? ConsensusPhase { get; init; }

    public int ConsensusSupport { get; init; }
    public int ConsensusSampleCount { get; init; }

    /// <summary>该屏是否形成了可信的既有图标晶格（样本足够且众数占比达标）。</summary>
    public bool ConsensusIsReliable { get; init; }

    /// <summary>
    /// 相位是否通过校验：可信晶格存在时必须与之一致；没有可信晶格时按探针自身落点判定。
    /// </summary>
    public bool PhaseVerified { get; init; }

    /// <summary>
    /// 该屏是否拿到了合格实测样本（因此可以写出一条 Measured 事实）。
    /// false 的两种情形：Explorer 不把探针快捷方式留在该屏（真机实测：seed 请求在 DISPLAY2、
    /// 落点却被异步回写到 DISPLAY1 的 (2,702)），或该屏主动落点样本不足（每轴至少 3 个）。
    /// 事实逐屏写：未实测屏不写事实，只记录 <see cref="UnmeasuredReason"/>；
    /// 整轮只有在"一块屏都测不到"或安全网不成立时才是 Failed。
    /// </summary>
    public bool IsMeasured { get; init; } = true;

    /// <summary>未完成实测的原因（仅 <see cref="IsMeasured"/> 为 false 时有值）。</summary>
    public string? UnmeasuredReason { get; init; }
}

public sealed record ExplorerGridProbeCleanupResult(
    string ShortcutStableId,
    PixelPoint OriginalPosition,
    PixelPoint? RestoreObservedPosition,
    bool RestoreAttempted,
    bool RestoreExact,
    string? Issue);

public sealed record ExplorerGridPhysicalProbeReport(
    ExplorerGridProbeRunStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<ExplorerGridProbeDisplayResult> Displays,
    IReadOnlyList<ExplorerGridProbeMove> Moves,
    IReadOnlyList<ExplorerGridProbePassiveSample> PassiveSamples,
    IReadOnlyList<ExplorerGridProbeCleanupResult> Cleanup,
    bool SharedSpacing,
    bool PhaseVerifiedPerDisplay,
    bool OwnedShortcutsDeleted,
    bool UserIconsUnchanged,
    string? Failure)
{
    /// <summary>
    /// 产生这批事实时的拓扑身份（不含 phase 派生值）。
    /// 事实只在它仍然匹配时有效。
    /// </summary>
    public string? TopologyIdentity { get; init; }

    /// <summary>
    /// 运行结束后检测到被 Explorer 布局回写挪动、并已恢复到运行前位置的既有桌面项目数量。
    /// 真机实测：Probe 的连续写入会让 Explorer 重排桌面图标流（整列前移一个槽位），
    /// 删除专用快捷方式不会让它自己恢复；因此探针必须在删掉自己的东西之后把用户图标放回去，
    /// 并用一次新的稳定 Observation 复核。<see cref="UserIconsUnchanged"/> 表示"恢复之后与运行前一致"。
    /// </summary>
    public int RepairedUserIconCount { get; init; }

    /// <summary>运行结束时仍然无法恢复到运行前位置的既有桌面项目数量（必须为 0 才可能 Passed）。</summary>
    public int UnrepairedUserIconCount { get; init; }

    public string Format()
    {
        var builder = new StringBuilder();
        builder.AppendLine("=== Explorer Grid Physical Probe v1 ===");
        builder.AppendLine($"Status: {Status}");
        builder.AppendLine($"StartedAtUtc: {StartedAtUtc:O}");
        builder.AppendLine($"FinishedAtUtc: {FinishedAtUtc:O}");
        builder.AppendLine($"TopologyIdentity: {TopologyIdentity ?? "—"}");
        builder.AppendLine($"SharedSpacing: {SharedSpacing}");
        builder.AppendLine($"PhaseVerifiedPerDisplay: {PhaseVerifiedPerDisplay}");
        builder.AppendLine($"MeasuredDisplays: {Displays.Count(static display => display.IsMeasured)}/{Displays.Count}");
        var unmeasuredDisplays = Displays.Where(static display => !display.IsMeasured).ToArray();
        if (unmeasuredDisplays.Length > 0)
        {
            builder.AppendLine(
                "UnmeasuredDisplays: " +
                string.Join(
                    "；",
                    unmeasuredDisplays.Select(static display =>
                        $"{display.DeviceName}（{display.UnmeasuredReason ?? "未记录"}）")));
        }
        builder.AppendLine($"OwnedShortcutsDeleted: {OwnedShortcutsDeleted}");
        builder.AppendLine($"UserIconsUnchanged: {UserIconsUnchanged}");
        builder.AppendLine($"UserIconsRepaired: {RepairedUserIconCount} unrepaired={UnrepairedUserIconCount}");
        if (!string.IsNullOrWhiteSpace(Failure))
            builder.AppendLine($"Failure: {Failure}");

        builder.AppendLine("Displays:");
        foreach (var display in Displays)
        {
            builder.AppendLine(
                $"  {display.DeviceName}: spacing=({display.Spacing.X},{display.Spacing.Y}) " +
                $"phase=({display.Phase.X},{display.Phase.Y}) " +
                $"horizontal={display.HorizontalSampleCount} vertical={display.VerticalSampleCount} " +
                $"corners={display.CornerSampleCount} " +
                $"active={display.ActiveScanSampleCount} passiveOccupied={display.PassiveOccupiedSampleCount} " +
                $"skippedLandings={display.SkippedLandingCount} " +
                $"spacingConstant={display.ObservedSpacingConstant} phaseConstant={display.ObservedPhaseConstant} " +
                $"phaseVerified={display.PhaseVerified} measured={display.IsMeasured}");
            if (!display.IsMeasured)
            {
                builder.AppendLine(
                    $"    unmeasured reason={display.UnmeasuredReason ?? "未记录"}");
            }
            var consensus = display.ConsensusIsReliable && display.ConsensusPhase is { } consensusPhase
                ? $"({consensusPhase.X},{consensusPhase.Y})"
                : "不可信";
            builder.AppendLine(
                $"    iconLattice consensus={consensus} " +
                $"support={display.ConsensusSupport}/{display.ConsensusSampleCount}");
        }

        builder.AppendLine("Moves (requested -> observed):");
        foreach (var move in Moves)
        {
            var settled = move.SettledObserved is { } point
                ? $"({point.X},{point.Y})"
                : "pending/unavailable";
            builder.AppendLine(
                $"  {move.DisplayDeviceName} {move.Kind}[{move.Ordinal}] " +
                $"previous=({move.Previous.X},{move.Previous.Y}) " +
                $"requested=({move.Requested.X},{move.Requested.Y}) " +
                $"immediateObserved=({move.Observed.X},{move.Observed.Y}) immediateExact={move.IsExact} " +
                $"settledObserved={settled} settledExact={move.SettledIsExact?.ToString() ?? "—"}");
        }

        builder.AppendLine("Passive occupied samples (read-only, no Writer call):");
        foreach (var sample in PassiveSamples)
        {
            builder.AppendLine(
                $"  {sample.DisplayDeviceName} {sample.Kind}[{sample.Ordinal}] " +
                $"observed=({sample.Observed.X},{sample.Observed.Y}) " +
                $"occupant={sample.OccupantStableId}");
        }

        builder.AppendLine("Cleanup:");
        foreach (var item in Cleanup)
        {
            var observed = item.RestoreObservedPosition is { } point
                ? $"({point.X},{point.Y})"
                : "—";
            builder.AppendLine(
                $"  {item.ShortcutStableId}: original=({item.OriginalPosition.X},{item.OriginalPosition.Y}) " +
                $"attempted={item.RestoreAttempted} observed={observed} exact={item.RestoreExact} " +
                $"issue={item.Issue ?? "—"}");
        }

        return builder.ToString();
    }
}

/// <summary>
/// 某块显示器既有桌面图标晶格的相位众数。它只用于交叉校验探针推断出的相位，
/// 不参与测量本身：晶格不可信时不拦截，可信但与探针相位不一致时停止（fail closed）。
/// </summary>
public sealed record ExplorerGridPhaseConsensus(
    bool IsReliable,
    PixelPoint? Phase,
    int Support,
    int SampleCount);

public interface IExplorerGridProbeShortcutStore
{
    Task<IReadOnlyList<ExplorerGridProbeShortcut>> CreateAsync(
        int count,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        CancellationToken cancellationToken);
}
