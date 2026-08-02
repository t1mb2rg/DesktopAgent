using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Preview;

public sealed class DesktopLayoutPreviewPlanTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so, int? max = null) =>
        new(id, n, b, so, ZonePlacementFlow.ColumnMajor, max);
    private static DesktopIconSnapshotItem I(int ord, string sid, string? mon = "D1", int px = 0, int py = 0) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(px, py), mon, false, true);
    private static readonly PixelRect WA = new(0, 0, 800, 640);
    private static readonly PixelPoint SP = new(80, 80);
    private static readonly DisplayDescriptor DD = new("D1", new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);

    private static (DesktopLayoutTemplate t, ResolvedDesktopLayoutTemplate rt, DesktopLayoutAssignmentPlan ap, DesktopLayoutPlacementPlan pp, List<DesktopIconSnapshotItem> input, List<DesktopIconSnapshotItem> target, List<DesktopIconSnapshotItem> oos, List<DesktopLayoutPreviewItem> items) ValidSetup()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var rt = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var input = new List<DesktopIconSnapshotItem> { I(0, "a"), I(1, "b") };
        var target = new List<DesktopIconSnapshotItem>(input); // 同一批实例
        var ap = DesktopLayoutAssignmentPlanner.Plan(rt, target);
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        var items = new List<DesktopLayoutPreviewItem>();
        foreach (var icon in target)
        {
            var placed = pp.Placements.FirstOrDefault(p => ReferenceEquals(p.Icon, icon));
            if (placed is not null)
                items.Add(DesktopLayoutPreviewItem.FromPlacement(icon, placed));
            else
                items.Add(DesktopLayoutPreviewItem.ForUnassigned(icon));
        }
        var oos = new List<DesktopIconSnapshotItem>();
        return (t, rt, ap, pp, input, target, oos, items);
    }

    [Fact] public void Valid_Accepted()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var plan = new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, oos, items);
        Assert.Equal(2, plan.InputIconCount);
    }

    [Fact] public void ResolvedTemplate_NotAssignmentPlanResolvedTemplate_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var t2 = new DesktopLayoutTemplate(Guid.NewGuid(), "T2", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var rt2 = DesktopLayoutTemplateResolver.Resolve(t2, WA, SP);
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t2, DD, rt2, ap, pp, input, target, oos, items));
    }

    [Fact] public void TargetNotSameInstance_AsAssignmentPlanInput_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var clone = target.Select(ic => I(ic.Ordinal, ic.StableId, mon: ic.MonitorDeviceName, px: ic.PixelPosition.X, py: ic.PixelPosition.Y)).ToList();
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, clone, oos, items));
    }

    [Fact] public void InputEqualsByValue_NotSameInstance_Fails()
    {
        var (t, rt, ap, pp, _, _, _, _) = ValidSetup();
        var badInput = new List<DesktopIconSnapshotItem> { I(0, "a"), I(1, "b") }; // 新实例
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, badInput, badInput, new List<DesktopIconSnapshotItem>(), new List<DesktopLayoutPreviewItem>()));
    }

    [Fact] public void TargetOutOfScope_ReferenceOverlap_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var overlapOos = new List<DesktopIconSnapshotItem> { target[0] }; // 同一实例
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, overlapOos, items));
    }

    [Fact] public void TargetOutOfScope_MissingInputRef_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var missing = new List<DesktopIconSnapshotItem> { I(9, "missing") };
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, missing, items));
    }

    [Fact] public void AssignedUnassigned_Swapped_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        // 交换：assigned 图标设为 Unassigned，反之亦然
        var swapped = new List<DesktopLayoutPreviewItem>
        {
            DesktopLayoutPreviewItem.ForUnassigned(target[0]), // 应为 assigned
            DesktopLayoutPreviewItem.FromPlacement(target[1], pp.Placements.First(p => ReferenceEquals(p.Icon, target[1])))
        };
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, oos, swapped));
    }

    [Fact] public void ExternalPlacement_NotFromPlacementPlan_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var t2 = new DesktopLayoutTemplate(Guid.NewGuid(), "T2", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var rt2 = DesktopLayoutTemplateResolver.Resolve(t2, WA, SP);
        var ap2 = DesktopLayoutAssignmentPlanner.Plan(rt2, new[] { target[0] });
        var pp2 = DesktopLayoutCoordinatePlanner.Plan(ap2);
        var badItems = new List<DesktopLayoutPreviewItem>
        {
            DesktopLayoutPreviewItem.FromPlacement(target[0], pp2.Placements[0]), // 来自不同 PlacementPlan
            items[1]
        };
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, oos, badItems));
    }

    [Fact] public void ItemPlacement_WrongTargetIcon_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        // items[0] uses target[1] but in position 0
        var wrong = new List<DesktopLayoutPreviewItem>
        {
            DesktopLayoutPreviewItem.FromPlacement(target[1], pp.Placements.First(p => ReferenceEquals(p.Icon, target[1]))),
            DesktopLayoutPreviewItem.FromPlacement(target[0], pp.Placements.First(p => ReferenceEquals(p.Icon, target[0])))
        };
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, oos, wrong));
    }

    [Fact] public void CurrentPosition_Wrong_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var badIcon = I(target[0].Ordinal, target[0].StableId, px: 999, py: 888);
        var badItems = new List<DesktopLayoutPreviewItem> { DesktopLayoutPreviewItem.ForUnassigned(badIcon), items[1] };
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, oos, badItems));
    }

    [Fact] public void TargetOrder_NotMatchAssignmentPlanInput_Fails()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var reversed = new List<DesktopIconSnapshotItem> { target[1], target[0] };
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, reversed, oos, items));
    }

    [Fact] public void MutateInputCollections_NoEffect()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var plan = new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, oos, items);
        input.Clear(); target.Clear(); oos.Clear(); items.Clear();
        Assert.Equal(2, plan.InputIconCount);
    }

    [Fact] public void AllPublicCollections_ReadOnly()
    {
        var (t, rt, ap, pp, input, target, oos, items) = ValidSetup();
        var plan = new DesktopLayoutPreviewPlan(t, DD, rt, ap, pp, input, target, oos, items);
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopLayoutPreviewItem>)plan.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconSnapshotItem>)plan.InputIcons).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconSnapshotItem>)plan.TargetDisplayIcons).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconSnapshotItem>)plan.OutOfScopeIcons).Clear());
    }
}
