using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 把一次全桌面语义理解编排成多份既有 DesktopLayoutEditDraft。
/// 默认保持当前物理显示器归属；只有 ConversationState 中的显式 category/item display target
/// 才允许改变目标屏，并通过 ExplicitInboundStableIds 接入既有跨屏 Preview/Writer 安全链。
/// </summary>
public static class DesktopUnderstandingMultiDisplayWorkspaceCompiler
{
    public sealed record DisplayCompilation(
        DisplayDescriptor Display,
        string DisplayRef,
        int ObservedIconCount,
        int SourceAssignmentCount,
        DesktopUnderstandingLayoutDraftCompiler.CompilationResult Layout,
        IReadOnlyList<DesktopReservedRegionConstraint> ReservedRegions)
    {
        public DesktopLayoutEditDraft Draft => Layout.Draft;
        public int MappedAssignmentCount => Layout.MappedAssignmentCount;
        public int UnmappedAssignmentCount => Layout.UnmappedAssignmentCount;
        public int ExplicitInboundCount => Draft.ExplicitInboundStableIds.Count;
    }

    public sealed record WorkspaceCompilationResult
    {
        public IReadOnlyList<DisplayCompilation> Displays { get; }
        public int SourceAssignmentCount { get; }
        public int ScopedAssignmentCount { get; }
        public int UnscopedAssignmentCount { get; }
        public int MappedAssignmentCount { get; }
        public int UnmappedAssignmentCount { get; }
        public int CrossDisplayAssignmentCount { get; }

        public WorkspaceCompilationResult(
            IReadOnlyList<DisplayCompilation> displays,
            int sourceAssignmentCount,
            int scopedAssignmentCount,
            int unscopedAssignmentCount,
            int crossDisplayAssignmentCount)
        {
            ArgumentNullException.ThrowIfNull(displays);
            Displays = Array.AsReadOnly(displays.ToArray());
            SourceAssignmentCount = sourceAssignmentCount;
            ScopedAssignmentCount = scopedAssignmentCount;
            UnscopedAssignmentCount = unscopedAssignmentCount;
            MappedAssignmentCount = Displays.Sum(static item => item.MappedAssignmentCount);
            UnmappedAssignmentCount = Displays.Sum(static item => item.UnmappedAssignmentCount) + unscopedAssignmentCount;
            CrossDisplayAssignmentCount = crossDisplayAssignmentCount;
        }
    }

    public static WorkspaceCompilationResult Compile(
        DesktopUnderstandingDraft understanding,
        DesktopObservation observation,
        string name = "AI 建议布局")
        => Compile(
            understanding,
            observation,
            DesktopLayoutConversationState.Empty,
            name);

    public static WorkspaceCompilationResult Compile(
        DesktopUnderstandingDraft understanding,
        DesktopObservation observation,
        DesktopLayoutConversationState conversationState,
        string name = "AI 建议布局")
    {
        ArgumentNullException.ThrowIfNull(understanding);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(conversationState);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));

        var displayMap = DesktopSemanticDisplayMap.Create(observation);
        var iconByStableId = observation.Icons.ToDictionary(
            static icon => icon.StableId,
            StringComparer.Ordinal);
        var categoryKeys = understanding.Categories
            .Select(static category => category.Key)
            .ToHashSet(StringComparer.Ordinal);
        var assignmentStableIds = understanding.Assignments
            .Select(static assignment => assignment.StableId)
            .ToHashSet(StringComparer.Ordinal);

        ValidateConversationState(
            conversationState,
            displayMap,
            categoryKeys,
            assignmentStableIds);

        var assignmentsByTargetDevice = new Dictionary<string, List<DesktopUnderstandingAssignment>>(
            StringComparer.Ordinal);
        var inboundByTargetDevice = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var outboundBySourceDevice = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var participatingDeviceNames = new HashSet<string>(StringComparer.Ordinal);
        var unscoped = 0;
        var crossDisplay = 0;

        foreach (var displayRef in conversationState.CategoryDisplayTargets.Values
                     .Concat(conversationState.ItemDisplayTargets.Values)
                     .Concat(conversationState.DisplayLoadPreferences.Keys)
                     .Concat(conversationState.ReservedRegions.Select(static region => region.DisplayRef)))
        {
            participatingDeviceNames.Add(displayMap.ResolveDisplayRef(displayRef).Display.DeviceName);
        }

        foreach (var assignment in understanding.Assignments)
        {
            if (!iconByStableId.TryGetValue(assignment.StableId, out var observedIcon)
                || !displayMap.TryResolveDeviceName(observedIcon.MonitorDeviceName, out var currentEntry)
                || currentEntry is null)
            {
                unscoped++;
                continue;
            }

            var targetDisplayRef = ResolveTargetDisplayRef(
                assignment,
                currentEntry.DisplayRef,
                conversationState);
            var targetEntry = displayMap.ResolveDisplayRef(targetDisplayRef);
            participatingDeviceNames.Add(currentEntry.Display.DeviceName);
            participatingDeviceNames.Add(targetEntry.Display.DeviceName);

            if (!assignmentsByTargetDevice.TryGetValue(targetEntry.Display.DeviceName, out var targetList))
            {
                targetList = [];
                assignmentsByTargetDevice.Add(targetEntry.Display.DeviceName, targetList);
            }
            targetList.Add(assignment);

            if (string.Equals(
                    currentEntry.Display.DeviceName,
                    targetEntry.Display.DeviceName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            crossDisplay++;
            AddStableId(inboundByTargetDevice, targetEntry.Display.DeviceName, assignment.StableId);
            AddStableId(outboundBySourceDevice, currentEntry.Display.DeviceName, assignment.StableId);
        }

        // 同一逻辑 Zone 若最终同时出现在多块屏幕，不能把同一个 ExistingZoneId
        // 复制为多个物理布局中的共享身份。
        var zoneKeysByDevice = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var mapEntry in displayMap.Entries)
        {
            if (!participatingDeviceNames.Contains(mapEntry.Display.DeviceName))
                continue;

            var usedZoneKeys = assignmentsByTargetDevice.TryGetValue(mapEntry.Display.DeviceName, out var targeted)
                ? targeted
                    .Where(static assignment => assignment.ZoneKey is not null)
                    .Select(static assignment => assignment.ZoneKey!)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            if (outboundBySourceDevice.TryGetValue(mapEntry.Display.DeviceName, out var outbound))
            {
                usedZoneKeys.UnionWith(understanding.Assignments
                    .Where(assignment => outbound.Contains(assignment.StableId) && assignment.ZoneKey is not null)
                    .Select(static assignment => assignment.ZoneKey!));
            }

            var explicitlyTargetedCategories = conversationState.CategoryDisplayTargets
                .Where(pair => string.Equals(pair.Value, mapEntry.DisplayRef, StringComparison.Ordinal))
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (explicitlyTargetedCategories.Count > 0)
            {
                usedZoneKeys.UnionWith(understanding.Zones
                    .Where(zone => zone.CategoryKeys.Any(explicitlyTargetedCategories.Contains))
                    .Select(static zone => zone.Key));
            }

            usedZoneKeys.UnionWith(conversationState.ItemDisplayTargets
                .Where(pair => string.Equals(pair.Value, mapEntry.DisplayRef, StringComparison.Ordinal))
                .Select(pair => understanding.Assignments.Single(assignment =>
                    string.Equals(assignment.StableId, pair.Key, StringComparison.Ordinal)).ZoneKey)
                .Where(static zoneKey => zoneKey is not null)
                .Select(static zoneKey => zoneKey!));

            // A reserved/load-only participant still needs local geometry, but no item assignments.
            if (usedZoneKeys.Count == 0 && (targeted is null || targeted.Count == 0))
                usedZoneKeys.UnionWith(understanding.Zones.Select(static zone => zone.Key));

            zoneKeysByDevice.Add(mapEntry.Display.DeviceName, usedZoneKeys);
        }

        var zoneDisplayCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var usedZoneKeys in zoneKeysByDevice.Values)
        {
            foreach (var zoneKey in usedZoneKeys)
            {
                zoneDisplayCounts.TryGetValue(zoneKey, out var count);
                zoneDisplayCounts[zoneKey] = count + 1;
            }
        }

        var results = new List<DisplayCompilation>();
        foreach (var mapEntry in displayMap.Entries)
        {
            var display = mapEntry.Display;
            if (!participatingDeviceNames.Contains(display.DeviceName))
                continue;
            var assignments = assignmentsByTargetDevice.TryGetValue(display.DeviceName, out var targeted)
                ? targeted
                : [];

            var usedZoneKeys = zoneKeysByDevice[display.DeviceName];
            if (usedZoneKeys.Count == 0)
            {
                throw new InvalidOperationException(
                    $"显示器语义引用 {mapEntry.DisplayRef} 上有 {assignments.Count} 个 Assignment，" +
                    "但没有任何可编译 ZoneKey。拒绝生成无分区布局。");
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
                    $"显示器 {mapEntry.DisplayRef} 的 Assignment 引用了未找到的 Zone：{string.Join(", ", missing)}。");
            }

            var requiredCategoryKeys = assignments
                .Select(static assignment => assignment.CategoryKey)
                .Concat(zones.SelectMany(static zone => zone.CategoryKeys))
                .ToHashSet(StringComparer.Ordinal);
            var categories = understanding.Categories
                .Where(category => requiredCategoryKeys.Contains(category.Key))
                .ToArray();

            var localUnderstanding = new DesktopUnderstandingDraft(
                conversationState.IntentSummary.Length == 0
                    ? understanding.IntentSummary
                    : conversationState.IntentSummary,
                understanding.DesktopSummary,
                categories,
                zones,
                assignments,
                understanding.Questions);

            var reservedForDisplay = conversationState.ReservedRegions
                .Where(region => string.Equals(
                    region.DisplayRef,
                    mapEntry.DisplayRef,
                    StringComparison.Ordinal))
                .ToArray();

            var layout = DesktopUnderstandingLayoutDraftCompiler.Compile(
                localUnderstanding,
                $"{name.Trim()} · {ShortDisplayName(display.DeviceName)}",
                TargetDisplayBinding.ForExactDeviceName(display.DeviceName),
                reservedForDisplay);

            var rules = layout.Draft.RuleSet.Rules.ToList();
            if (outboundBySourceDevice.TryGetValue(display.DeviceName, out var outbound))
            {
                foreach (var stableId in outbound.OrderBy(static value => value, StringComparer.Ordinal))
                {
                    if (rules.Any(rule => string.Equals(rule.StableId, stableId, StringComparison.Ordinal)))
                        continue;
                    rules.Add(DesktopLayoutIconRule.ForExclude(stableId));
                }
            }

            var inbound = inboundByTargetDevice.TryGetValue(display.DeviceName, out var inboundSet)
                ? inboundSet.OrderBy(static value => value, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            var updatedRuleSet = new DesktopLayoutIconRuleSet(layout.Draft.TemplateId, rules);
            var updatedDraft = layout.Draft.WithRuleSet(updatedRuleSet, inbound);
            layout = new DesktopUnderstandingLayoutDraftCompiler.CompilationResult(
                updatedDraft,
                layout.SourceAssignmentCount,
                layout.MappedAssignmentCount,
                layout.UnmappedAssignmentCount);

            results.Add(new DisplayCompilation(
                display,
                mapEntry.DisplayRef,
                mapEntry.CurrentItemCount,
                assignments.Count,
                layout,
                reservedForDisplay));
        }

        var scoped = results.Sum(static item => item.SourceAssignmentCount);
        if (scoped + unscoped != understanding.Assignments.Count)
            throw new InvalidOperationException("多显示器 Assignment 目标分桶计数不守恒。");

        return new WorkspaceCompilationResult(
            results,
            understanding.Assignments.Count,
            scoped,
            unscoped,
            crossDisplay);
    }

    private static string ResolveTargetDisplayRef(
        DesktopUnderstandingAssignment assignment,
        string currentDisplayRef,
        DesktopLayoutConversationState state)
    {
        if (state.ItemDisplayTargets.TryGetValue(assignment.StableId, out var itemTarget))
            return itemTarget;
        if (state.CategoryDisplayTargets.TryGetValue(assignment.CategoryKey, out var categoryTarget))
            return categoryTarget;
        return currentDisplayRef;
    }

    private static void ValidateConversationState(
        DesktopLayoutConversationState state,
        DesktopSemanticDisplayMap displayMap,
        IReadOnlySet<string> knownCategoryKeys,
        IReadOnlySet<string> knownStableIds)
    {
        var displayRefs = displayMap.Entries
            .Select(static entry => entry.DisplayRef)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var pair in state.CategoryDisplayTargets)
        {
            if (!knownCategoryKeys.Contains(pair.Key))
                throw new InvalidOperationException($"布局状态引用了未知 CategoryKey \"{pair.Key}\"。");
            RequireDisplayRef(pair.Value, displayRefs);
        }

        foreach (var pair in state.ItemDisplayTargets)
        {
            if (!knownStableIds.Contains(pair.Key))
                throw new InvalidOperationException($"布局状态引用了未知 StableId \"{pair.Key}\"。");
            RequireDisplayRef(pair.Value, displayRefs);
        }

        foreach (var displayRef in state.DisplayLoadPreferences.Keys)
            RequireDisplayRef(displayRef, displayRefs);
        foreach (var reserved in state.ReservedRegions)
            RequireDisplayRef(reserved.DisplayRef, displayRefs);
    }

    private static void RequireDisplayRef(string displayRef, IReadOnlySet<string> known)
    {
        if (!known.Contains(displayRef))
            throw new InvalidOperationException($"布局状态引用了当前不存在的 display_ref \"{displayRef}\"。");
    }

    private static void AddStableId(
        IDictionary<string, HashSet<string>> map,
        string deviceName,
        string stableId)
    {
        if (!map.TryGetValue(deviceName, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map.Add(deviceName, set);
        }
        set.Add(stableId);
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
