using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Preview;

/// <summary>
/// 纯 Core 布局预览规划器。串联 M2-02A/B/C，生成位置差异预览。
/// 不涉及 Writer、SQLite、WPF。
/// </summary>
public static class DesktopLayoutPreviewPlanner
{
    public static DesktopLayoutPreviewPlan Plan(
        DesktopLayoutTemplate template,
        IReadOnlyList<DisplayDescriptor> displays,
        string? selectedTargetDeviceName,
        PixelPoint iconSpacing,
        IReadOnlyList<DesktopIconSnapshotItem> icons)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (displays is null) throw new ArgumentNullException(nameof(displays));
        if (icons is null) throw new ArgumentNullException(nameof(icons));
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须 > 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须 > 0，实际 {iconSpacing.Y}。");

        // 验证图标
        var inputList = new List<DesktopIconSnapshotItem>(icons.Count);
        var seenOrdinals = new HashSet<int>();
        var seenStableIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < icons.Count; i++)
        {
            if (icons[i] is null)
                throw new ArgumentException($"Icons[{i}] 为 null。", nameof(icons));
            if (icons[i].Ordinal < 0)
                throw new ArgumentOutOfRangeException(nameof(icons),
                    $"Icons[{i}] Ordinal 为负数 ({icons[i].Ordinal})。");

            if (!seenOrdinals.Add(icons[i].Ordinal))
                throw new ArgumentException(
                    $"Icons Ordinal {icons[i].Ordinal} 重复。", nameof(icons));
            if (!seenStableIds.Add(icons[i].StableId))
                throw new ArgumentException(
                    $"Icons StableId \"{icons[i].StableId}\" 重复。", nameof(icons));

            inputList.Add(icons[i]);
        }

        // 按 Ordinal ASC 排序
        inputList.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        // 解析目标显示器
        var targetDisplay = DesktopLayoutTargetDisplayResolver.Resolve(
            template.TargetDisplayBinding, displays, selectedTargetDeviceName);

        // 筛选目标显示器图标
        var targetIcons = new List<DesktopIconSnapshotItem>();
        var oosIcons = new List<DesktopIconSnapshotItem>();
        foreach (var icon in inputList)
        {
            if (string.Equals(icon.MonitorDeviceName, targetDisplay.DeviceName, StringComparison.Ordinal))
                targetIcons.Add(icon);
            else
                oosIcons.Add(icon);
        }

        // M2-02A: 解析模板
        var resolvedTemplate = DesktopLayoutTemplateResolver.Resolve(
            template, targetDisplay.WorkArea, iconSpacing);

        // M2-02B: 分配图标
        var assignmentPlan = DesktopLayoutAssignmentPlanner.Plan(
            resolvedTemplate, targetIcons);

        // M2-02C: 坐标规划
        var placementPlan = DesktopLayoutCoordinatePlanner.Plan(assignmentPlan);

        // 构造 PreviewItems
        var previewItems = new List<DesktopLayoutPreviewItem>();
        var placementByOrdinal = new Dictionary<int, DesktopIconTargetPlacement>();
        foreach (var p in placementPlan.Placements)
            placementByOrdinal[p.Icon.Ordinal] = p;

        var unassignedOrdinals = new HashSet<int>(
            placementPlan.UnassignedIcons.Select(u => u.Ordinal));

        foreach (var icon in targetIcons)
        {
            if (placementByOrdinal.TryGetValue(icon.Ordinal, out var placement))
            {
                previewItems.Add(DesktopLayoutPreviewItem.FromPlacement(icon, placement));
            }
            else if (unassignedOrdinals.Contains(icon.Ordinal))
            {
                previewItems.Add(DesktopLayoutPreviewItem.ForUnassigned(icon));
            }
            else
            {
                throw new InvalidOperationException(
                    $"图标 (Ordinal={icon.Ordinal}, StableId=\"{icon.StableId}\") " +
                    "既不在 Placements 中也不在 UnassignedIcons 中。");
            }
        }

        return new DesktopLayoutPreviewPlan(
            template, targetDisplay, resolvedTemplate,
            assignmentPlan, placementPlan,
            inputList, targetIcons, oosIcons,
            previewItems);
    }
}
