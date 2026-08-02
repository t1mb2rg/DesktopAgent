using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Preview;

/// <summary>
/// 单个图标在当前预览中的位置差异。
/// </summary>
public sealed record DesktopLayoutPreviewItem
{
    public DesktopIconSnapshotItem Icon { get; }
    public DesktopIconTargetPlacement? Placement { get; }
    public DesktopLayoutPreviewItemState State { get; }

    public PixelPoint CurrentPosition { get; }
    public PixelPoint? TargetPosition { get; }
    public long? DeltaX { get; }
    public long? DeltaY { get; }

    public bool IsAssigned { get; }
    public bool RequiresMove { get; }

    private DesktopLayoutPreviewItem(
        DesktopIconSnapshotItem icon,
        DesktopIconTargetPlacement? placement,
        DesktopLayoutPreviewItemState state,
        PixelPoint currentPosition,
        PixelPoint? targetPosition,
        long? deltaX,
        long? deltaY,
        bool isAssigned,
        bool requiresMove)
    {
        Icon = icon;
        Placement = placement;
        State = state;
        CurrentPosition = currentPosition;
        TargetPosition = targetPosition;
        DeltaX = deltaX;
        DeltaY = deltaY;
        IsAssigned = isAssigned;
        RequiresMove = requiresMove;
    }

    /// <summary>从已分配的 Placement 创建。icon 必须与 placement.Icon 是同一实例。</summary>
    public static DesktopLayoutPreviewItem FromPlacement(
        DesktopIconSnapshotItem icon,
        DesktopIconTargetPlacement placement)
    {
        if (icon is null) throw new ArgumentNullException(nameof(icon));
        if (placement is null) throw new ArgumentNullException(nameof(placement));

        if (!ReferenceEquals(icon, placement.Icon))
            throw new ArgumentException(
                "icon 与 placement.Icon 不是同一实例。" +
                $" icon.StableId=\"{icon.StableId}\", placement.Icon.StableId=\"{placement.Icon.StableId}\"。",
                nameof(icon));

        var current = icon.PixelPosition;
        var target = placement.TargetPosition;

        if (current == target)
        {
            return new DesktopLayoutPreviewItem(
                icon, placement,
                DesktopLayoutPreviewItemState.AlreadyAtTarget,
                current, target, 0L, 0L,
                isAssigned: true, requiresMove: false);
        }
        else
        {
            long dX = (long)target.X - current.X;
            long dY = (long)target.Y - current.Y;
            return new DesktopLayoutPreviewItem(
                icon, placement,
                DesktopLayoutPreviewItemState.MoveRequired,
                current, target, dX, dY,
                isAssigned: true, requiresMove: true);
        }
    }

    /// <summary>从未分配的图标创建。</summary>
    public static DesktopLayoutPreviewItem ForUnassigned(
        DesktopIconSnapshotItem icon)
    {
        if (icon is null) throw new ArgumentNullException(nameof(icon));

        return new DesktopLayoutPreviewItem(
            icon, null,
            DesktopLayoutPreviewItemState.Unassigned,
            icon.PixelPosition, null, null, null,
            isAssigned: false, requiresMove: false);
    }
}
