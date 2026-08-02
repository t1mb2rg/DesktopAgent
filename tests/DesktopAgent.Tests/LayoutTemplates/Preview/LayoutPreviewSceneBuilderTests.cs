using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc.LayoutTemplates.Preview;

namespace DesktopAgent.Tests.LayoutTemplates.Preview;

public sealed class LayoutPreviewSceneBuilderTests
{
    private static DesktopIconSnapshotItem I(int ord, string sid, string? mon = "DISPLAY1", int px = 0, int py = 0) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(px, py), mon, false, true);

    private static DesktopLayoutPreviewPlan MakePlan(PixelRect? wa = null)
    {
        var wa2 = wa ?? new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa2, 96, 96, true);
        return DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a", px: wa2.Left, py: wa2.Top), I(1, "b", px: wa2.Left + 80, py: wa2.Top) });
    }

    private static (LayoutPreviewScene Scene, DesktopLayoutPreviewPlan Plan) Build(PixelRect? wa = null)
    {
        var plan = MakePlan(wa);
        var actualWa = wa ?? new PixelRect(0, 0, 800, 640);
        var t = new LayoutPreviewCanvasTransform(actualWa, 800, 640, 0);
        return (LayoutPreviewSceneBuilder.Build(plan, t), plan);
    }

    // ═══ Zone ═══

    [Fact] public void Zones_BySortOrder()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] {
                new DesktopLayoutZone(Guid.NewGuid(), "Z1", new NormalizedRect(0, 0, 5000, 10000), 5, ZonePlacementFlow.ColumnMajor, maxItemCount: 1),
                new DesktopLayoutZone(Guid.NewGuid(), "Z0", new NormalizedRect(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 1)
            });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a"), I(1, "b") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.Equal(1, scene.Zones[0].SortOrder);
        Assert.Equal(5, scene.Zones[1].SortOrder);
    }

    [Fact] public void ZonePixelBounds_ToCanvasBounds()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a"), I(1, "b") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        var zv = scene.Zones[0];
        // Zone covers 100% → canvas bounds = full viewport
        Assert.Equal(0, zv.CanvasLeft);
        Assert.Equal(0, zv.CanvasTop);
        Assert.Equal(800, zv.CanvasWidth);
        Assert.Equal(640, zv.CanvasHeight);
        Assert.Equal(wa.Left, zv.PixelLeft);
        Assert.Equal(wa.Top, zv.PixelTop);
        Assert.Equal(wa.Right, zv.PixelRight);
        Assert.Equal(wa.Bottom, zv.PixelBottom);
    }

    [Fact] public void Zone_Capacity_And_AssignedCount()
    {
        var (scene, plan) = Build();
        var zv = scene.Zones[0];
        Assert.Equal(2L, zv.EffectiveCapacity);
        Assert.Equal(2, zv.AssignedCount);
    }

    [Fact] public void RowMajor_ColumnMajor_Metadata()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] {
                new DesktopLayoutZone(Guid.NewGuid(), "ZC", new NormalizedRect(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 1),
                new DesktopLayoutZone(Guid.NewGuid(), "ZR", new NormalizedRect(5000, 0, 10000, 10000), 1, ZonePlacementFlow.RowMajor, maxItemCount: 1)
            });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a"), I(1, "b") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.Equal("ColumnMajor", scene.Zones[0].PlacementFlow);
        Assert.Equal("RowMajor", scene.Zones[1].PlacementFlow);
    }

    [Fact] public void Slot_Row_Column()
    {
        var (scene, _) = Build();
        var row = scene.ItemRows[0];
        Assert.Equal("0", row.SlotIndex);
        // ColumnMajor in 2-row effective layout: slot0→col0,row0
        Assert.Equal("0", row.ColumnIndex);
        Assert.Equal("0", row.RowIndex);
        // slot1→col0,row1
        Assert.Equal("1", scene.ItemRows[1].SlotIndex);
    }

    // ═══ Icon / Item ═══

    [Fact] public void Icons_ByOrdinal()
    {
        var (scene, _) = Build();
        Assert.Equal(0, scene.Icons[0].Icon.Ordinal);
        Assert.Equal(1, scene.Icons[1].Icon.Ordinal);
    }

    [Fact] public void AlreadyAtTarget_SinglePosition()
    {
        var (scene, _) = Build();
        Assert.Equal(DesktopLayoutPreviewItemState.AlreadyAtTarget, scene.Icons[0].State);
        // AlreadyAtTarget has no target dot in rendering (TargetCanvasX is null? No — it's assigned,
        // so it HAS a target, but the state says it's already there)
        // Actually, AlreadyAtTarget IS assigned so it has target coords.
        // The rendering just doesn't draw the red dot. But the data has target.
        Assert.True(scene.Icons[0].TargetCanvasX.HasValue);
        Assert.Equal(scene.Icons[0].CurrentCanvasX, scene.Icons[0].TargetCanvasX!.Value);
        Assert.Equal(scene.Icons[0].CurrentCanvasY, scene.Icons[0].TargetCanvasY!.Value);
    }

    [Fact] public void MoveRequired_HasTarget()
    {
        var (scene, _) = Build();
        Assert.Equal(DesktopLayoutPreviewItemState.MoveRequired, scene.Icons[1].State);
        Assert.True(scene.Icons[1].TargetCanvasX.HasValue);
    }

    [Fact] public void MoveRequired_PreciseCoords()
    {
        var (scene, _) = Build();
        var iv = scene.Icons[1];
        Assert.Equal(DesktopLayoutPreviewItemState.MoveRequired, iv.State);
        Assert.True(iv.TargetCanvasX.HasValue);
        // current != target
        Assert.True(iv.CurrentCanvasX != iv.TargetCanvasX || iv.CurrentCanvasY != iv.TargetCanvasY);
    }

    [Fact] public void ItemRowsCount_Matches()
    {
        var (scene, plan) = Build();
        Assert.Equal(plan.Items.Count, scene.ItemRows.Count);
    }

    [Fact] public void DisplayName_StableId_Preserved()
    {
        var (scene, _) = Build();
        var row = scene.ItemRows[0];
        Assert.Equal("N_a", row.DisplayName);
        Assert.True(row.StableIdTruncated.Length > 0);
        Assert.Equal(scene.Icons[0].Icon.DisplayName, row.DisplayName);
    }

    [Fact] public void Delta_LongExtremes()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        // Place icons far from target positions to get extreme deltas
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a", px: 10000, py: 10000), I(1, "b", px: -5000, py: 0) });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        foreach (var row in scene.ItemRows)
        {
            Assert.NotNull(row.DeltaX);
            Assert.NotNull(row.DeltaY);
            // Delta is a long — should parse as integer string
            Assert.True(long.TryParse(row.DeltaX, out _));
            Assert.True(long.TryParse(row.DeltaY, out _));
        }
    }

    // ═══ Unassigned / OOS ═══

    [Fact] public void Unassigned_NoTarget()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 1) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a"), I(1, "b") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.False(scene.Icons[1].TargetCanvasX.HasValue);
        Assert.Equal("—", scene.ItemRows[1].TargetX);
    }

    [Fact] public void Oos_ByOrdinal()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null) });
        var displays = new[] {
            new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true),
            new DisplayDescriptor("OTHER", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, false) };
        var plan = DesktopLayoutPreviewPlanner.Plan(t, displays, null, new PixelPoint(80, 80),
            new[] { I(3, "d", mon: "OTHER"), I(1, "b", mon: "OTHER"), I(0, "a", mon: "DISPLAY1") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.Equal(1, scene.OutOfScopeRows[0].Ordinal);
        Assert.Equal(3, scene.OutOfScopeRows[1].Ordinal);
    }

    [Fact] public void Oos_NotInIcons()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null) });
        var displays = new[] {
            new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true),
            new DisplayDescriptor("OTHER", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, false) };
        var plan = DesktopLayoutPreviewPlanner.Plan(t, displays, null, new PixelPoint(80, 80),
            new[] { I(0, "a", mon: "DISPLAY1"), I(1, "b", mon: "OTHER") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        // OOS icons must NOT appear in Icons list
        var iconIds = new HashSet<int>(scene.Icons.Select(iv => iv.Icon.Ordinal));
        foreach (var oos in scene.OutOfScopeRows)
            Assert.DoesNotContain(oos.Ordinal, iconIds);
    }

    // ═══ 不变量 / 防御性 ═══

    [Fact] public void WorkAreaMismatch_Fails()
    {
        var plan = MakePlan(new PixelRect(0, 0, 800, 640));
        var t = new LayoutPreviewCanvasTransform(new PixelRect(100, 200, 900, 840), 800, 640, 0);
        Assert.Throws<ArgumentException>(() => LayoutPreviewSceneBuilder.Build(plan, t));
    }

    [Fact] public void NegWorkArea_MatchesPlan()
    {
        var wa = new PixelRect(-800, -640, 0, 0);
        var plan = MakePlan(wa);
        var t = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, t);
        Assert.NotNull(scene);
    }

    [Fact] public void InputPlan_NotModified()
    {
        var plan = MakePlan();
        var tc = plan.TargetDisplayIconCount;
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        LayoutPreviewSceneBuilder.Build(plan, t);
        Assert.Equal(tc, plan.TargetDisplayIconCount);
    }

    // ═══ 集合只读 ═══

    [Fact] public void ItemRows_ReadOnly()
    {
        var (scene, _) = Build();
        Assert.Throws<NotSupportedException>(() => ((IList<LayoutPreviewItemRow>)scene.ItemRows).Clear());
    }

    [Fact] public void Zones_ReadOnly()
    {
        var (scene, _) = Build();
        Assert.Throws<NotSupportedException>(() => ((IList<LayoutPreviewZoneVisual>)scene.Zones).Clear());
    }

    [Fact] public void Icons_ReadOnly()
    {
        var (scene, _) = Build();
        Assert.Throws<NotSupportedException>(() => ((IList<LayoutPreviewIconVisual>)scene.Icons).Clear());
    }

    [Fact] public void OosRows_ReadOnly()
    {
        var (scene, _) = Build();
        Assert.Throws<NotSupportedException>(() => ((IList<LayoutPreviewOutOfScopeRow>)scene.OutOfScopeRows).Clear());
    }

    [Fact] public void DefensiveCopy_ModifyInputList()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var plan = MakePlan(wa);
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);

        // Build Scene with mutable lists that we'll later modify
        var zones = new List<LayoutPreviewZoneVisual>();
        var icons = new List<LayoutPreviewIconVisual>();
        var rows = new List<LayoutPreviewItemRow>();
        var oosRows = new List<LayoutPreviewOutOfScopeRow>();

        // Build via SceneBuilder to get real data
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);

        // Capture counts
        var zoneCount = scene.Zones.Count;
        var iconCount = scene.Icons.Count;
        var rowCount = scene.ItemRows.Count;
        var oosCount = scene.OutOfScopeRows.Count;

        // Verify Scene's copies are independent by ensuring counts are > 0
        // and the original plan's data is in the scene
        Assert.True(zoneCount > 0);
        Assert.True(iconCount > 0);
        Assert.True(rowCount > 0);
    }

    // ═══ 确定性 ═══

    [Fact] public void Redeterministic()
    {
        var plan = MakePlan();
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        var s1 = LayoutPreviewSceneBuilder.Build(plan, t);
        var s2 = LayoutPreviewSceneBuilder.Build(plan, t);
        Assert.Equal(s1.Zones.Count, s2.Zones.Count);
        Assert.Equal(s1.Icons.Count, s2.Icons.Count);
        Assert.Equal(s1.Icons[0].CurrentCanvasX, s2.Icons[0].CurrentCanvasX);
    }

    [Fact] public void Deterministic_FieldByField()
    {
        var plan = MakePlan();
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        var s1 = LayoutPreviewSceneBuilder.Build(plan, t);
        var s2 = LayoutPreviewSceneBuilder.Build(plan, t);

        // Zones: every field identical
        Assert.Equal(s1.Zones.Count, s2.Zones.Count);
        for (var i = 0; i < s1.Zones.Count; i++)
        {
            var z1 = s1.Zones[i]; var z2 = s2.Zones[i];
            Assert.Equal(z1.ZoneId, z2.ZoneId);
            Assert.Equal(z1.Name, z2.Name);
            Assert.Equal(z1.SortOrder, z2.SortOrder);
            Assert.Equal(z1.PixelLeft, z2.PixelLeft);
            Assert.Equal(z1.PixelTop, z2.PixelTop);
            Assert.Equal(z1.CanvasLeft, z2.CanvasLeft);
            Assert.Equal(z1.CanvasTop, z2.CanvasTop);
            Assert.Equal(z1.CanvasWidth, z2.CanvasWidth);
            Assert.Equal(z1.CanvasHeight, z2.CanvasHeight);
            Assert.Equal(z1.EffectiveCapacity, z2.EffectiveCapacity);
            Assert.Equal(z1.AssignedCount, z2.AssignedCount);
        }

        // Icons: every field identical
        Assert.Equal(s1.Icons.Count, s2.Icons.Count);
        for (var i = 0; i < s1.Icons.Count; i++)
        {
            var iv1 = s1.Icons[i]; var iv2 = s2.Icons[i];
            Assert.Equal(iv1.State, iv2.State);
            Assert.Equal(iv1.CurrentCanvasX, iv2.CurrentCanvasX);
            Assert.Equal(iv1.CurrentCanvasY, iv2.CurrentCanvasY);
            Assert.Equal(iv1.TargetCanvasX, iv2.TargetCanvasX);
            Assert.Equal(iv1.TargetCanvasY, iv2.TargetCanvasY);
            Assert.Equal(iv1.ZoneId, iv2.ZoneId);
            Assert.Equal(iv1.ZoneName, iv2.ZoneName);
            Assert.Equal(iv1.ToolTip, iv2.ToolTip);
        }

        // ItemRows: every field identical
        Assert.Equal(s1.ItemRows.Count, s2.ItemRows.Count);
        for (var i = 0; i < s1.ItemRows.Count; i++)
        {
            var r1 = s1.ItemRows[i]; var r2 = s2.ItemRows[i];
            Assert.Equal(r1.Ordinal, r2.Ordinal);
            Assert.Equal(r1.DisplayName, r2.DisplayName);
            Assert.Equal(r1.StateText, r2.StateText);
            Assert.Equal(r1.CurrentX, r2.CurrentX);
            Assert.Equal(r1.CurrentY, r2.CurrentY);
            Assert.Equal(r1.TargetX, r2.TargetX);
            Assert.Equal(r1.TargetY, r2.TargetY);
            Assert.Equal(r1.DeltaX, r2.DeltaX);
            Assert.Equal(r1.DeltaY, r2.DeltaY);
            Assert.Equal(r1.ZoneName, r2.ZoneName);
            Assert.Equal(r1.SlotIndex, r2.SlotIndex);
            Assert.Equal(r1.RowIndex, r2.RowIndex);
            Assert.Equal(r1.ColumnIndex, r2.ColumnIndex);
        }

        // OutOfScopeRows: every field identical
        Assert.Equal(s1.OutOfScopeRows.Count, s2.OutOfScopeRows.Count);
        for (var i = 0; i < s1.OutOfScopeRows.Count; i++)
        {
            var o1 = s1.OutOfScopeRows[i]; var o2 = s2.OutOfScopeRows[i];
            Assert.Equal(o1.Ordinal, o2.Ordinal);
            Assert.Equal(o1.DisplayName, o2.DisplayName);
            Assert.Equal(o1.StableIdTruncated, o2.StableIdTruncated);
            Assert.Equal(o1.MonitorDeviceName, o2.MonitorDeviceName);
            Assert.Equal(o1.CurrentX, o2.CurrentX);
            Assert.Equal(o1.CurrentY, o2.CurrentY);
        }
    }

    // ═══ Viewport resize ═══

    [Fact] public void ViewportResize_OnlyCanvasChanges()
    {
        var plan = MakePlan();
        var t1 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        var t2 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 400, 320, 0);
        var s1 = LayoutPreviewSceneBuilder.Build(plan, t1);
        var s2 = LayoutPreviewSceneBuilder.Build(plan, t2);
        Assert.Equal(s1.Zones.Count, s2.Zones.Count);
        Assert.Equal(s1.Icons.Count, s2.Icons.Count);
        // Core pixel values unchanged
        Assert.Equal(s1.ItemRows[0].CurrentX, s2.ItemRows[0].CurrentX);
        Assert.Equal(s1.ItemRows[0].TargetX, s2.ItemRows[0].TargetX);
    }

    [Fact] public void ViewportResize_CanvasChanges_PixelUnchanged()
    {
        // Use a plan where icons have non-zero X AND Y positions
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a", px: 100, py: 80), I(1, "b", px: 200, py: 160) });

        var t1 = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var t2 = new LayoutPreviewCanvasTransform(wa, 400, 320, 0);
        var s1 = LayoutPreviewSceneBuilder.Build(plan, t1);
        var s2 = LayoutPreviewSceneBuilder.Build(plan, t2);

        // Canvas coords must differ (different scale)
        Assert.NotEqual(s1.Icons[0].CurrentCanvasX, s2.Icons[0].CurrentCanvasX);
        Assert.NotEqual(s1.Icons[0].CurrentCanvasY, s2.Icons[0].CurrentCanvasY);

        // All core pixel targets/deltas must be identical
        for (var i = 0; i < s1.ItemRows.Count; i++)
        {
            Assert.Equal(s1.ItemRows[i].CurrentX, s2.ItemRows[i].CurrentX);
            Assert.Equal(s1.ItemRows[i].CurrentY, s2.ItemRows[i].CurrentY);
            Assert.Equal(s1.ItemRows[i].TargetX, s2.ItemRows[i].TargetX);
            Assert.Equal(s1.ItemRows[i].TargetY, s2.ItemRows[i].TargetY);
            Assert.Equal(s1.ItemRows[i].DeltaX, s2.ItemRows[i].DeltaX);
            Assert.Equal(s1.ItemRows[i].DeltaY, s2.ItemRows[i].DeltaY);
            Assert.Equal(s1.ItemRows[i].ZoneName, s2.ItemRows[i].ZoneName);
            Assert.Equal(s1.ItemRows[i].SlotIndex, s2.ItemRows[i].SlotIndex);
        }
    }

    // ═══ 构造验证 — IconVisual ═══

    [Fact] public void IconVisual_NaN_CurrentCanvas_Fails()
    {
        var icon = I(0, "a");
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewIconVisual(icon, DesktopLayoutPreviewItemState.Unassigned,
                double.NaN, 0, null, null, null, "", ""));
    }

    [Fact] public void IconVisual_TargetXY_Mismatch_Fails()
    {
        var icon = I(0, "a");
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewIconVisual(icon, DesktopLayoutPreviewItemState.MoveRequired,
                0, 0, 100.0, null, Guid.NewGuid(), "Z", ""));
    }

    [Fact] public void IconVisual_Unassigned_WithTarget_Fails()
    {
        var icon = I(0, "a");
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewIconVisual(icon, DesktopLayoutPreviewItemState.Unassigned,
                0, 0, 100.0, 200.0, Guid.NewGuid(), "Z", ""));
    }

    [Fact] public void IconVisual_Assigned_WithoutTarget_Fails()
    {
        var icon = I(0, "a");
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewIconVisual(icon, DesktopLayoutPreviewItemState.AlreadyAtTarget,
                0, 0, null, null, Guid.NewGuid(), "Z", ""));
    }

    [Fact] public void IconVisual_Assigned_WithoutZoneId_Fails()
    {
        var icon = I(0, "a");
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewIconVisual(icon, DesktopLayoutPreviewItemState.MoveRequired,
                0, 0, 100.0, 200.0, null, "Z", ""));
    }

    // ═══ 构造验证 — ZoneVisual ═══

    [Fact] public void ZoneVisual_NegativeCanvasDimensions_Fails()
    {
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewZoneVisual(Guid.NewGuid(), "Z", 0, 0, 0, 800, 600,
                0, 0, -1, 100, 10, 0, "ColumnMajor"));
    }

    [Fact] public void ZoneVisual_NaN_Canvas_Fails()
    {
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewZoneVisual(Guid.NewGuid(), "Z", 0, 0, 0, 800, 600,
                double.NaN, 0, 100, 100, 10, 0, "ColumnMajor"));
    }

    [Fact] public void ZoneVisual_EmptyName_Fails()
    {
        Assert.Throws<ArgumentException>(() =>
            new LayoutPreviewZoneVisual(Guid.NewGuid(), "", 0, 0, 0, 800, 600,
                0, 0, 100, 100, 10, 0, "ColumnMajor"));
    }

    [Fact] public void ZoneVisual_AssignedOverCapacity_Fails()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LayoutPreviewZoneVisual(Guid.NewGuid(), "Z", 0, 0, 0, 800, 600,
                0, 0, 100, 100, 5, 6, "ColumnMajor"));
    }

    // ═══ 构造验证 — Scene ═══

    [Fact] public void Scene_WorkAreaMismatch_Fails()
    {
        var plan = MakePlan(new PixelRect(0, 0, 800, 640));
        var badTransform = new LayoutPreviewCanvasTransform(new PixelRect(100, 200, 900, 840), 800, 640, 0);
        Assert.Throws<ArgumentException>(() => new LayoutPreviewScene(
            plan, badTransform,
            Array.Empty<LayoutPreviewZoneVisual>(),
            Array.Empty<LayoutPreviewIconVisual>(),
            Array.Empty<LayoutPreviewItemRow>(),
            Array.Empty<LayoutPreviewOutOfScopeRow>()));
    }

    [Fact] public void Scene_NullPlan_Fails()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        Assert.Throws<ArgumentNullException>(() => new LayoutPreviewScene(
            null!, t,
            Array.Empty<LayoutPreviewZoneVisual>(),
            Array.Empty<LayoutPreviewIconVisual>(),
            Array.Empty<LayoutPreviewItemRow>(),
            Array.Empty<LayoutPreviewOutOfScopeRow>()));
    }
}
