using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

public sealed record LayoutPreviewZoneVisual(
    Guid ZoneId,
    string Name,
    int SortOrder,
    int PixelLeft, int PixelTop, int PixelRight, int PixelBottom,
    double CanvasLeft, double CanvasTop, double CanvasWidth, double CanvasHeight,
    long EffectiveCapacity,
    int AssignedCount,
    string PlacementFlow);

public sealed record LayoutPreviewIconVisual(
    DesktopIconSnapshotItem Icon,
    DesktopLayoutPreviewItemState State,
    double CurrentCanvasX,
    double CurrentCanvasY,
    double? TargetCanvasX,
    double? TargetCanvasY,
    Guid? ZoneId,
    string ZoneName,
    string ToolTip);

public sealed record LayoutPreviewScene(
    DesktopLayoutPreviewPlan Plan,
    LayoutPreviewCanvasTransform Transform,
    IReadOnlyList<LayoutPreviewZoneVisual> Zones,
    IReadOnlyList<LayoutPreviewIconVisual> Icons,
    IReadOnlyList<LayoutPreviewItemRow> ItemRows,
    IReadOnlyList<LayoutPreviewOutOfScopeRow> OutOfScopeRows);
