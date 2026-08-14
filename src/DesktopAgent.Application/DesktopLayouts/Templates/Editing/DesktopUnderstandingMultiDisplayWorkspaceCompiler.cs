using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 把一次全桌面 AI 语义理解按同一次 fresh DesktopObservation 的真实显示器归属
/// 编排成多份既有 DesktopLayoutEditDraft。
///
/// 多显示器只存在于产品/Application 编排层；每个 Draft 仍然绑定一个精确显示器，
/// 因此 Preview / Candidate / Recovery / Writer 继续以单显示器布局作为安全执行原子。
/// 物理显示器信息只在本地参与分桶，不进入 M4 Provider 合同。
/// </summary>
public static class DesktopUnderstandingMultiDisplayWorkspaceCompiler
{
    public sealed record DisplayCompilation(
        DisplayDescriptor Display,
        int ObservedIconCount,
        int SourceAssignmentCount,
        DesktopUnderstandingLayoutDraftCompiler.CompilationResult Layout)
    {
        public DesktopLayoutEditDraft Draft => Layout.Draft;
        public int MappedAssignmentCount => Layout.MappedAssignmentCount;
        public int UnmappedAssignmentCount => Layout.UnmappedAssignmentCount;
    }

    public sealed record WorkspaceCompilationResult
    {
        public IReadOnlyList<DisplayCompilation> Displays { get; }
        public int SourceAssignmentCount { get; }
        public int ScopedAssignmentCount { get; }
        public int UnscopedAssignmentCount { get; }
        public int MappedAssignmentCount { get; }
        public int UnmappedAssignmentCount { get; }

        public WorkspaceCompilationResult(
            IReadOnlyList<DisplayCompilation> displays,
            int sourceAssignmentCount,
            int scopedAssignmentCount,
            int unscopedAssignmentCount)
        {
            ArgumentNullException.ThrowIfNull(displays);
            Displays = Array.AsReadOnly(displays.ToArray());
            SourceAssignmentCount = sourceAssignmentCount;
            ScopedAssignmentCount = scopedAssignmentCount;
            UnscopedAssignmentCount = unscopedAssignmentCount;
            MappedAssignmentCount = Displays.Sum(static item => item.MappedAssignmentCount);
            UnmappedAssignmentCount = Displays.Sum(static item => item.UnmappedAssignmentCount) + unscopedAssignmentCount;
        }
    }

    public static WorkspaceCompilationResult Compile(
        DesktopUnderstandingDraft understanding,
        DesktopObservation observation,
        string name = "AI 建议布局")
    {
        ArgumentNullException.ThrowIfNull(understanding);
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));

        var displayByName = observation.Displays.ToDictionary(
            static display => display.DeviceName,
            StringComparer.Ordinal);
        var iconByStableId = observation.Icons.ToDictionary(
            static icon => icon.StableId,
            StringComparer.Ordinal);

        var assignmentsByDisplay = new Dictionary<string, List<DesktopUnderstandingAssignment>>(
            StringComparer.Ordinal);
        var unscoped = 0;

        foreach (var assignment in understanding.Assignments)
        {
            if (!iconByStableId.TryGetValue(assignment.StableId, out var observedIcon)
                || observedIcon.MonitorDeviceName is null
                || !displayByName.ContainsKey(observedIcon.MonitorDeviceName))
            {
                unscoped++;
                continue;
            }

            if (!assignmentsByDisplay.TryGetValue(observedIcon.MonitorDeviceName, out var list))
            {
                list = new List<DesktopUnderstandingAssignment>();
                assignmentsByDisplay.Add(observedIcon.MonitorDeviceName, list);
            }
            list.Add(assignment);
        }

        // 同一逻辑 Zone 若同时出现在多块屏幕，不能把同一个 ExistingZoneId
        // 复制成多个物理布局中的共享身份。仅在 Zone 只属于一块屏时保留复用提示。
        var zoneDisplayCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in assignmentsByDisplay)
        {
            foreach (var zoneKey in pair.Value
                         .Where(static assignment => assignment.ZoneKey is not null)
                         .Select(static assignment => assignment.ZoneKey!)
                         .Distinct(StringComparer.Ordinal))
            {
                zoneDisplayCounts.TryGetValue(zoneKey, out var count);
                zoneDisplayCounts[zoneKey] = count + 1;
            }
        }

        var results = new List<DisplayCompilation>();
        foreach (var display in observation.Displays
                     .OrderByDescending(static item => item.IsPrimary)
                     .ThenBy(static item => item.DeviceName, StringComparer.Ordinal))
        {
            if (!assignmentsByDisplay.TryGetValue(display.DeviceName, out var assignments)
                || assignments.Count == 0)
            {
                continue;
            }

            var usedZoneKeys = assignments
                .Where(static assignment => assignment.ZoneKey is not null)
                .Select(static assignment => assignment.ZoneKey!)
                .ToHashSet(StringComparer.Ordinal);

            if (usedZoneKeys.Count == 0)
            {
                throw new InvalidOperationException(
                    $"显示器 {display.DeviceName} 上有 {assignments.Count} 个 AI Assignment，" +
                    "但没有任何可编译的 ZoneKey。拒绝生成无分区的显示器布局。");
            }

            var zones = understanding.Zones
                .Where(zone => usedZoneKeys.Contains(zone.Key))
                .Select(zone => CloneZoneForDisplay(
                    zone,
                    preserveExistingZoneId: zoneDisplayCounts.TryGetValue(zone.Key, out var count) && count == 1))
                .ToArray();

            if (zones.Length != usedZoneKeys.Count)
            {
                var knownKeys = zones.Select(static zone => zone.Key).ToHashSet(StringComparer.Ordinal);
                var missing = usedZoneKeys.Where(key => !knownKeys.Contains(key));
                throw new InvalidOperationException(
                    $"显示器 {display.DeviceName} 的 Assignment 引用了未找到的 Zone：{string.Join(", ", missing)}。");
            }

            var requiredCategoryKeys = assignments
                .Select(static assignment => assignment.CategoryKey)
                .Concat(zones.SelectMany(static zone => zone.CategoryKeys))
                .ToHashSet(StringComparer.Ordinal);
            var categories = understanding.Categories
                .Where(category => requiredCategoryKeys.Contains(category.Key))
                .ToArray();

            var localUnderstanding = new DesktopUnderstandingDraft(
                understanding.IntentSummary,
                understanding.DesktopSummary,
                categories,
                zones,
                assignments,
                understanding.Questions);

            var layout = DesktopUnderstandingLayoutDraftCompiler.Compile(
                localUnderstanding,
                $"{name.Trim()} · {ShortDisplayName(display.DeviceName)}",
                TargetDisplayBinding.ForExactDeviceName(display.DeviceName));

            var observedIconCount = observation.Icons.Count(icon =>
                string.Equals(icon.MonitorDeviceName, display.DeviceName, StringComparison.Ordinal));

            results.Add(new DisplayCompilation(
                display,
                observedIconCount,
                assignments.Count,
                layout));
        }

        var scoped = results.Sum(static item => item.SourceAssignmentCount);
        if (scoped + unscoped != understanding.Assignments.Count)
            throw new InvalidOperationException("多显示器 Assignment 分桶计数不守恒。");

        return new WorkspaceCompilationResult(
            results,
            understanding.Assignments.Count,
            scoped,
            unscoped);
    }

    private static DesktopUnderstandingZoneDraft CloneZoneForDisplay(
        DesktopUnderstandingZoneDraft zone,
        bool preserveExistingZoneId)
        => new(
            zone.Key,
            zone.Name,
            preserveExistingZoneId ? zone.ExistingZoneId : null,
            zone.PlacementHint,
            zone.SizeHint,
            zone.CategoryKeys);

    private static string ShortDisplayName(string deviceName)
    {
        const string prefix = @"\\.\";
        return deviceName.StartsWith(prefix, StringComparison.Ordinal)
            ? deviceName[prefix.Length..]
            : deviceName;
    }
}
