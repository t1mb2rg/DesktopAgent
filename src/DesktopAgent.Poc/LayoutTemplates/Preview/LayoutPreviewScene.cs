using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

/// <summary>Zone 可视化模型。</summary>
public sealed record LayoutPreviewZoneVisual
{
    public Guid ZoneId { get; init; }
    public string Name { get; init; } = "";
    public int SortOrder { get; init; }
    public int PixelLeft { get; init; }
    public int PixelTop { get; init; }
    public int PixelRight { get; init; }
    public int PixelBottom { get; init; }
    public double CanvasLeft { get; init; }
    public double CanvasTop { get; init; }
    public double CanvasWidth { get; init; }
    public double CanvasHeight { get; init; }
    public long EffectiveCapacity { get; init; }
    public int AssignedCount { get; init; }
    public string PlacementFlow { get; init; } = "";
}

/// <summary>图标可视化模型。</summary>
public sealed record LayoutPreviewIconVisual
{
    public DesktopIconSnapshotItem Icon { get; init; } = null!;
    public DesktopLayoutPreviewItemState State { get; init; }
    public double CurrentCanvasX { get; init; }
    public double CurrentCanvasY { get; init; }
    public double TargetCanvasX { get; init; }
    public double TargetCanvasY { get; init; }
    public Guid? ZoneId { get; init; }
    public string ZoneName { get; init; } = "";
    public string ToolTip { get; init; } = "";
}

/// <summary>
/// 只读 Scene 模型，供 Canvas 渲染消费。
/// </summary>
public sealed record LayoutPreviewScene
{
    public DesktopLayoutPreviewPlan Plan { get; init; } = null!;
    public LayoutPreviewCanvasTransform Transform { get; init; } = null!;
    public IReadOnlyList<LayoutPreviewZoneVisual> Zones { get; init; } = Array.Empty<LayoutPreviewZoneVisual>();
    public IReadOnlyList<LayoutPreviewIconVisual> Icons { get; init; } = Array.Empty<LayoutPreviewIconVisual>();
    public IReadOnlyList<LayoutPreviewItemRow> ItemRows { get; init; } = Array.Empty<LayoutPreviewItemRow>();
    public IReadOnlyList<LayoutPreviewOutOfScopeRow> OutOfScopeRows { get; init; } = Array.Empty<LayoutPreviewOutOfScopeRow>();
}
