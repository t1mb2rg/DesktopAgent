using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

/// <summary>
/// Zone 可视化数据 — 不可变 sealed class，构造时校验。
/// </summary>
public sealed class LayoutPreviewZoneVisual
{
    public Guid ZoneId { get; }
    public string Name { get; }
    public int SortOrder { get; }
    public int PixelLeft { get; }
    public int PixelTop { get; }
    public int PixelRight { get; }
    public int PixelBottom { get; }
    public double CanvasLeft { get; }
    public double CanvasTop { get; }
    public double CanvasWidth { get; }
    public double CanvasHeight { get; }
    public long EffectiveCapacity { get; }
    public int AssignedCount { get; }
    public string PlacementFlow { get; }

    public LayoutPreviewZoneVisual(
        Guid zoneId, string name, int sortOrder,
        int pixelLeft, int pixelTop, int pixelRight, int pixelBottom,
        double canvasLeft, double canvasTop, double canvasWidth, double canvasHeight,
        long effectiveCapacity, int assignedCount, string placementFlow)
    {
        if (zoneId == Guid.Empty)
            throw new ArgumentException("ZoneId 不能为 Guid.Empty。", nameof(zoneId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Zone Name 不能为空。", nameof(name));
        if (pixelRight <= pixelLeft || pixelBottom <= pixelTop)
            throw new ArgumentException(
                $"Pixel bounds 必须为正面积：({pixelLeft},{pixelTop})-({pixelRight},{pixelBottom})。");
        if (!double.IsFinite(canvasLeft) || !double.IsFinite(canvasTop)
            || !double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight))
            throw new ArgumentException("Zone 的 Canvas 坐标必须为有限数值。");
        if (canvasWidth < 0 || canvasHeight < 0)
            throw new ArgumentException("Zone 的 Canvas 宽高不能为负数。");
        if (effectiveCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(effectiveCapacity));
        if (assignedCount < 0 || assignedCount > effectiveCapacity)
            throw new ArgumentOutOfRangeException(nameof(assignedCount),
                $"AssignedCount ({assignedCount}) 超出 EffectiveCapacity ({effectiveCapacity})。");
        if (string.IsNullOrWhiteSpace(placementFlow))
            throw new ArgumentException("PlacementFlow 不能为空。", nameof(placementFlow));

        ZoneId = zoneId;
        Name = name;
        SortOrder = sortOrder;
        PixelLeft = pixelLeft;
        PixelTop = pixelTop;
        PixelRight = pixelRight;
        PixelBottom = pixelBottom;
        CanvasLeft = canvasLeft;
        CanvasTop = canvasTop;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        EffectiveCapacity = effectiveCapacity;
        AssignedCount = assignedCount;
        PlacementFlow = placementFlow;
    }
}

/// <summary>
/// 图标可视化数据 — 不可变 sealed class，构造时校验 TargetX/TargetY 成对、State 一致性、有限数值。
/// </summary>
public sealed class LayoutPreviewIconVisual
{
    public DesktopIconSnapshotItem Icon { get; }
    public DesktopLayoutPreviewItemState State { get; }
    public double CurrentCanvasX { get; }
    public double CurrentCanvasY { get; }
    public double? TargetCanvasX { get; }
    public double? TargetCanvasY { get; }
    public Guid? ZoneId { get; }
    public string ZoneName { get; }
    public string ToolTip { get; }

    public LayoutPreviewIconVisual(
        DesktopIconSnapshotItem icon,
        DesktopLayoutPreviewItemState state,
        double currentCanvasX, double currentCanvasY,
        double? targetCanvasX, double? targetCanvasY,
        Guid? zoneId, string zoneName, string toolTip)
    {
        if (icon is null) throw new ArgumentNullException(nameof(icon));
        if (!Enum.IsDefined(state))
            throw new ArgumentException($"无效的 State 枚举值：{(int)state}。", nameof(state));
        if (!double.IsFinite(currentCanvasX) || !double.IsFinite(currentCanvasY))
            throw new ArgumentException("Current Canvas 坐标必须为有限数值。");

        // Target X/Y 必须成对出现
        if (targetCanvasX.HasValue != targetCanvasY.HasValue)
            throw new ArgumentException(
                "TargetCanvasX 和 TargetCanvasY 必须同时为 null 或同时有值。");
        if (targetCanvasX.HasValue && (!double.IsFinite(targetCanvasX.Value) || !double.IsFinite(targetCanvasY!.Value)))
            throw new ArgumentException("Target Canvas 坐标必须为有限数值。");

        // Unassigned 不能有 target / ZoneId / ZoneName
        if (state == DesktopLayoutPreviewItemState.Unassigned)
        {
            if (targetCanvasX.HasValue)
                throw new ArgumentException("Unassigned 状态不能有 Target Canvas 坐标。");
            if (zoneId is not null)
                throw new ArgumentException("Unassigned 状态不能有 ZoneId。");
            if (!string.IsNullOrEmpty(zoneName))
                throw new ArgumentException("Unassigned 状态 ZoneName 必须为空。");
        }
        else
        {
            // Assigned (AlreadyAtTarget / MoveRequired) 必须有 target
            if (!targetCanvasX.HasValue)
                throw new ArgumentException("非 Unassigned 状态必须有 Target Canvas 坐标。");
            if (zoneId is null || zoneId.Value == Guid.Empty)
                throw new ArgumentException("Assigned 图标必须有非空 ZoneId。");
            if (string.IsNullOrWhiteSpace(zoneName))
                throw new ArgumentException("Assigned 图标 ZoneName 不能为空。");
        }

        Icon = icon;
        State = state;
        CurrentCanvasX = currentCanvasX;
        CurrentCanvasY = currentCanvasY;
        TargetCanvasX = targetCanvasX;
        TargetCanvasY = targetCanvasY;
        ZoneId = zoneId;
        ZoneName = zoneName ?? "";
        ToolTip = toolTip ?? "";
    }
}

/// <summary>
/// 布局预览场景 — 不可变 sealed class。
/// 构造时验证不变量并对所有集合防御性复制。
/// </summary>
public sealed class LayoutPreviewScene
{
    public DesktopLayoutPreviewPlan? Plan { get; }
    public DesktopLayoutRuleAwarePreviewPlan? RuleAwarePlan { get; }
    public LayoutPreviewCanvasTransform Transform { get; }
    public IReadOnlyList<LayoutPreviewZoneVisual> Zones { get; }
    public IReadOnlyList<LayoutPreviewIconVisual> Icons { get; }
    public IReadOnlyList<LayoutPreviewItemRow> ItemRows { get; }
    public IReadOnlyList<LayoutPreviewOutOfScopeRow> OutOfScopeRows { get; }

    public LayoutPreviewScene(
        DesktopLayoutPreviewPlan plan,
        LayoutPreviewCanvasTransform transform,
        IReadOnlyList<LayoutPreviewZoneVisual> zones,
        IReadOnlyList<LayoutPreviewIconVisual> icons,
        IReadOnlyList<LayoutPreviewItemRow> itemRows,
        IReadOnlyList<LayoutPreviewOutOfScopeRow> outOfScopeRows)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (transform is null) throw new ArgumentNullException(nameof(transform));
        if (zones is null) throw new ArgumentNullException(nameof(zones));
        if (icons is null) throw new ArgumentNullException(nameof(icons));
        if (itemRows is null) throw new ArgumentNullException(nameof(itemRows));
        if (outOfScopeRows is null) throw new ArgumentNullException(nameof(outOfScopeRows));
        if (transform.WorkArea != plan.TargetDisplay.WorkArea)
            throw new ArgumentException(
                "Transform.WorkArea 必须与 Plan.TargetDisplay.WorkArea 完全一致。",
                nameof(transform));

        if (zones.Count != plan.ResolvedTemplate.Zones.Count)
            throw new ArgumentException("Zones 数量不一致。", nameof(zones));
        if (icons.Count != plan.Items.Count)
            throw new ArgumentException("Icons 数量不一致。", nameof(icons));
        if (itemRows.Count != plan.Items.Count)
            throw new ArgumentException("ItemRows 数量不一致。", nameof(itemRows));
        if (outOfScopeRows.Count != plan.OutOfScopeIcons.Count)
            throw new ArgumentException("OutOfScopeRows 数量不一致。", nameof(outOfScopeRows));

        Plan = plan;
        RuleAwarePlan = null;
        Transform = transform;
        Zones = zones.ToList().AsReadOnly();
        Icons = icons.ToList().AsReadOnly();
        ItemRows = itemRows.ToList().AsReadOnly();
        OutOfScopeRows = outOfScopeRows.ToList().AsReadOnly();
    }

    /// <summary>规则感知场景构造器。</summary>
    internal LayoutPreviewScene(
        DesktopLayoutRuleAwarePreviewPlan ruleAwarePlan,
        LayoutPreviewCanvasTransform transform,
        IReadOnlyList<LayoutPreviewZoneVisual> zones,
        IReadOnlyList<LayoutPreviewIconVisual> icons,
        IReadOnlyList<LayoutPreviewItemRow> itemRows,
        IReadOnlyList<LayoutPreviewOutOfScopeRow> outOfScopeRows)
    {
        if (ruleAwarePlan is null) throw new ArgumentNullException(nameof(ruleAwarePlan));
        if (transform is null) throw new ArgumentNullException(nameof(transform));
        if (zones is null) throw new ArgumentNullException(nameof(zones));
        if (icons is null) throw new ArgumentNullException(nameof(icons));
        if (itemRows is null) throw new ArgumentNullException(nameof(itemRows));
        if (outOfScopeRows is null) throw new ArgumentNullException(nameof(outOfScopeRows));

        Plan = null;
        RuleAwarePlan = ruleAwarePlan;
        Transform = transform;
        Zones = zones.ToList().AsReadOnly();
        Icons = icons.ToList().AsReadOnly();
        ItemRows = itemRows.ToList().AsReadOnly();
        OutOfScopeRows = outOfScopeRows.ToList().AsReadOnly();
    }
}
