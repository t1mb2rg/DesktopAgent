using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopObservations;

public enum DesktopObservationStabilityStatus
{
    Stable = 0,
    Unstable = 1,
}

public enum DesktopObservationStabilityIssueCode
{
    TopologyChanged = 0,
    AutoArrangeChanged = 1,
    IconSpacingChanged = 2,
    IconAdded = 3,
    IconRemoved = 4,
    IconChanged = 5,
}

public sealed record DesktopObservationStabilityIssue(
    DesktopObservationStabilityIssueCode Code,
    string Message,
    string? StableId = null);

/// <summary>
/// 两次完整 DesktopObservation 之间的纯稳定性判断。
/// Stable 只表示连续 observation 描述同一当前事实；不表示任何历史 Writer 是否成功。
/// </summary>
public sealed record DesktopObservationStabilityResult
{
    public DesktopObservationStabilityStatus Status { get; }
    public DesktopObservation? StableObservation { get; }
    public IReadOnlyList<DesktopObservationStabilityIssue> Issues { get; }

    public bool IsStable => Status == DesktopObservationStabilityStatus.Stable;

    public DesktopObservationStabilityResult(
        DesktopObservationStabilityStatus status,
        DesktopObservation? stableObservation,
        IReadOnlyList<DesktopObservationStabilityIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (status == DesktopObservationStabilityStatus.Stable && stableObservation is null)
            throw new ArgumentException("Stable 状态必须提供 StableObservation。", nameof(stableObservation));
        if (status != DesktopObservationStabilityStatus.Stable && stableObservation is not null)
            throw new ArgumentException("非 Stable 状态不得提供 StableObservation。", nameof(stableObservation));
        if (status == DesktopObservationStabilityStatus.Unstable && issues.Count == 0)
            throw new ArgumentException("Unstable 状态至少需要一个 issue。", nameof(issues));

        Status = status;
        StableObservation = stableObservation;
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}

/// <summary>
/// M3 的全桌面稳定性比较器。
/// 忽略 ObservationId、观察时间、读取耗时和 Shell 连接路径等诊断元数据；
/// 比较真正影响当前桌面事实的 topology、Auto Arrange、IconSpacing 与完整图标事实。
/// </summary>
public static class DesktopObservationStabilityEvaluator
{
    public static DesktopObservationStabilityResult Evaluate(
        DesktopObservation first,
        DesktopObservation second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var issues = new List<DesktopObservationStabilityIssue>();

        if (!DisplayTopologyComparer.AreEquivalent(first.Displays, second.Displays))
        {
            issues.Add(new DesktopObservationStabilityIssue(
                DesktopObservationStabilityIssueCode.TopologyChanged,
                "连续两次完整观察之间显示拓扑发生变化。"));
        }

        if (first.PositioningCapabilities.IsAutoArrangeEnabled
            != second.PositioningCapabilities.IsAutoArrangeEnabled)
        {
            issues.Add(new DesktopObservationStabilityIssue(
                DesktopObservationStabilityIssueCode.AutoArrangeChanged,
                "连续两次完整观察之间 Auto Arrange 状态发生变化。"));
        }

        if (first.PositioningCapabilities.Spacing != second.PositioningCapabilities.Spacing)
        {
            issues.Add(new DesktopObservationStabilityIssue(
                DesktopObservationStabilityIssueCode.IconSpacingChanged,
                "连续两次完整观察之间 IconSpacing 发生变化。"));
        }

        var firstIcons = first.Icons.ToDictionary(icon => icon.StableId, StringComparer.Ordinal);
        var secondIcons = second.Icons.ToDictionary(icon => icon.StableId, StringComparer.Ordinal);

        foreach (var stableId in firstIcons.Keys
                     .Except(secondIcons.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            issues.Add(new DesktopObservationStabilityIssue(
                DesktopObservationStabilityIssueCode.IconRemoved,
                $"StableId “{stableId}”只存在于第一次观察。",
                stableId));
        }

        foreach (var stableId in secondIcons.Keys
                     .Except(firstIcons.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            issues.Add(new DesktopObservationStabilityIssue(
                DesktopObservationStabilityIssueCode.IconAdded,
                $"StableId “{stableId}”只存在于第二次观察。",
                stableId));
        }

        foreach (var stableId in firstIcons.Keys
                     .Intersect(secondIcons.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!GetSemanticFact(firstIcons[stableId]).Equals(GetSemanticFact(secondIcons[stableId])))
            {
                issues.Add(new DesktopObservationStabilityIssue(
                    DesktopObservationStabilityIssueCode.IconChanged,
                    $"StableId “{stableId}”在连续两次完整观察之间发生变化。",
                    stableId));
            }
        }

        if (issues.Count > 0)
        {
            return new DesktopObservationStabilityResult(
                DesktopObservationStabilityStatus.Unstable,
                null,
                issues);
        }

        // Stable observation 始终采用第二次 fresh observation。
        return new DesktopObservationStabilityResult(
            DesktopObservationStabilityStatus.Stable,
            second,
            Array.Empty<DesktopObservationStabilityIssue>());
    }

    private static DesktopIconSemanticFact GetSemanticFact(DesktopIconDescriptor icon) =>
        new(
            icon.DisplayName,
            icon.ParsingName,
            icon.ItemKind,
            icon.PixelPosition,
            icon.MonitorDeviceName,
            icon.IsSystemItem,
            icon.IsFileSystemItem);

    private sealed record DesktopIconSemanticFact(
        string DisplayName,
        string? ParsingName,
        ItemKind ItemKind,
        Geometry.PixelPoint PixelPosition,
        string? MonitorDeviceName,
        bool IsSystemItem,
        bool IsFileSystemItem);
}
