using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 两次恢复观察之间的稳定性结论。
/// Stable 只表示“当前事实在连续观察中一致”，不表示历史 Writer 是否成功。
/// </summary>
public enum RecoveryObservationStabilityStatus
{
    Stable = 0,
    Unstable = 1,
    Unavailable = 2,
}

public enum RecoveryObservationStabilityIssueCode
{
    IncompleteDesktopRead = 0,
    DuplicateDisplayDeviceName = 1,
    TargetDisplayMissing = 2,
    TopologyChanged = 3,
    RelevantIconChanged = 4,
}

public sealed record RecoveryObservationStabilityIssue(
    RecoveryObservationStabilityIssueCode Code,
    string? StableId,
    string Message);

/// <summary>
/// 一次恢复观察样本：同一时刻读取到的显示拓扑与桌面图标状态。
/// Displays 与 Desktop.Icons 均在构造时复制，避免后续外部集合修改污染观察证据。
/// </summary>
public sealed record RecoveryObservationSample
{
    public IReadOnlyList<DisplayDescriptor> Displays { get; }
    public DesktopIconReadResult Desktop { get; }

    public RecoveryObservationSample(
        IReadOnlyList<DisplayDescriptor> displays,
        DesktopIconReadResult desktop)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(desktop);

        Displays = Array.AsReadOnly(displays.ToArray());
        Desktop = new DesktopIconReadResult(
            Array.AsReadOnly(desktop.Icons.ToArray()),
            desktop.FailedItemCount,
            desktop.ConnectionMethod,
            desktop.ReadTime,
            desktop.PrimaryPathException);
    }
}

/// <summary>
/// Stable Observe 阶段的纯数据结果。
/// StableSample 仅在 Status == Stable 时存在，并取第二次 fresh observation。
/// </summary>
public sealed record RecoveryObservationStabilityResult
{
    public RecoveryObservationStabilityStatus Status { get; }
    public RecoveryObservationSample? StableSample { get; }
    public IReadOnlyList<RecoveryObservationStabilityIssue> Issues { get; }

    public bool IsStable => Status == RecoveryObservationStabilityStatus.Stable;

    public RecoveryObservationStabilityResult(
        RecoveryObservationStabilityStatus status,
        RecoveryObservationSample? stableSample,
        IReadOnlyList<RecoveryObservationStabilityIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (status == RecoveryObservationStabilityStatus.Stable && stableSample is null)
            throw new ArgumentException("Stable 状态必须提供 StableSample。", nameof(stableSample));
        if (status != RecoveryObservationStabilityStatus.Stable && stableSample is not null)
            throw new ArgumentException("非 Stable 状态不得提供 StableSample。", nameof(stableSample));

        Status = status;
        StableSample = stableSample;
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}

/// <summary>
/// 比较连续两次当前状态观察，判断恢复决策是否拥有稳定事实基础。
/// 不读取 Journal，不调用 Writer，不推断历史。
/// </summary>
public static class RecoveryObservationStabilityEvaluator
{
    public static RecoveryObservationStabilityResult Evaluate(
        TargetDisplayLayoutExecutionPlan executionPlan,
        RecoveryObservationSample first,
        RecoveryObservationSample second)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var availabilityIssues = new List<RecoveryObservationStabilityIssue>();

        AddReadAvailabilityIssues(first, "第一次", availabilityIssues);
        AddReadAvailabilityIssues(second, "第二次", availabilityIssues);
        AddTopologyAvailabilityIssues(executionPlan.TargetDeviceName, first.Displays, "第一次", availabilityIssues);
        AddTopologyAvailabilityIssues(executionPlan.TargetDeviceName, second.Displays, "第二次", availabilityIssues);

        if (availabilityIssues.Count > 0)
        {
            return new RecoveryObservationStabilityResult(
                RecoveryObservationStabilityStatus.Unavailable,
                null,
                availabilityIssues);
        }

        if (!AreTopologiesEquivalent(first.Displays, second.Displays))
        {
            return new RecoveryObservationStabilityResult(
                RecoveryObservationStabilityStatus.Unstable,
                null,
                new[]
                {
                    new RecoveryObservationStabilityIssue(
                        RecoveryObservationStabilityIssueCode.TopologyChanged,
                        null,
                        "连续两次观察之间显示拓扑发生变化，当前物理坐标系不稳定。")
                });
        }

        var changedIssues = new List<RecoveryObservationStabilityIssue>();
        var relevantStableIds = executionPlan.Steps
            .Select(step => step.StableId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        foreach (var stableId in relevantStableIds)
        {
            var firstFacts = GetRelevantFacts(first.Desktop, stableId);
            var secondFacts = GetRelevantFacts(second.Desktop, stableId);

            if (!firstFacts.SequenceEqual(secondFacts))
            {
                changedIssues.Add(new RecoveryObservationStabilityIssue(
                    RecoveryObservationStabilityIssueCode.RelevantIconChanged,
                    stableId,
                    $"StableId “{stableId}”在连续两次观察之间发生变化。"));
            }
        }

        if (changedIssues.Count > 0)
        {
            return new RecoveryObservationStabilityResult(
                RecoveryObservationStabilityStatus.Unstable,
                null,
                changedIssues);
        }

        return new RecoveryObservationStabilityResult(
            RecoveryObservationStabilityStatus.Stable,
            second,
            Array.Empty<RecoveryObservationStabilityIssue>());
    }

    /// <summary>
    /// 判断两个显示拓扑是否描述同一物理坐标系。列表顺序不影响结果。
    /// </summary>
    public static bool AreTopologiesEquivalent(
        IReadOnlyList<DisplayDescriptor> expected,
        IReadOnlyList<DisplayDescriptor> current)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);

        if (expected.Count != current.Count)
            return false;

        var left = expected.OrderBy(display => display.DeviceName, StringComparer.Ordinal).ToArray();
        var right = current.OrderBy(display => display.DeviceName, StringComparer.Ordinal).ToArray();
        return left.SequenceEqual(right);
    }

    private static void AddReadAvailabilityIssues(
        RecoveryObservationSample sample,
        string ordinal,
        ICollection<RecoveryObservationStabilityIssue> issues)
    {
        if (sample.Desktop.FailedItemCount == 0)
            return;

        issues.Add(new RecoveryObservationStabilityIssue(
            RecoveryObservationStabilityIssueCode.IncompleteDesktopRead,
            null,
            $"{ordinal}桌面读取不完整：FailedItemCount={sample.Desktop.FailedItemCount}。"));
    }

    private static void AddTopologyAvailabilityIssues(
        string targetDeviceName,
        IReadOnlyList<DisplayDescriptor> displays,
        string ordinal,
        ICollection<RecoveryObservationStabilityIssue> issues)
    {
        var duplicateNames = displays
            .GroupBy(display => display.DeviceName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var duplicateName in duplicateNames)
        {
            issues.Add(new RecoveryObservationStabilityIssue(
                RecoveryObservationStabilityIssueCode.DuplicateDisplayDeviceName,
                null,
                $"{ordinal}显示拓扑中 DeviceName “{duplicateName}”重复，无法建立唯一显示器身份。"));
        }

        if (!displays.Any(display => string.Equals(
                display.DeviceName,
                targetDeviceName,
                StringComparison.Ordinal)))
        {
            issues.Add(new RecoveryObservationStabilityIssue(
                RecoveryObservationStabilityIssueCode.TargetDisplayMissing,
                null,
                $"{ordinal}显示拓扑中缺少目标显示器 “{targetDeviceName}”。"));
        }
    }

    private static IReadOnlyList<RelevantIconFact> GetRelevantFacts(
        DesktopIconReadResult desktop,
        string stableId)
        => desktop.Icons
            .Where(icon => string.Equals(icon.StableId, stableId, StringComparison.Ordinal))
            .Select(icon => new RelevantIconFact(icon.PixelPosition, icon.MonitorDeviceName))
            .OrderBy(fact => fact.Position.X)
            .ThenBy(fact => fact.Position.Y)
            .ThenBy(fact => fact.MonitorDeviceName, StringComparer.Ordinal)
            .ToArray();

    private sealed record RelevantIconFact(
        PixelPoint Position,
        string? MonitorDeviceName);
}
