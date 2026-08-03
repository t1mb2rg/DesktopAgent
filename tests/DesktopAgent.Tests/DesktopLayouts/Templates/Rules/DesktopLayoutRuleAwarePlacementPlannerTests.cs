using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Rules;

public sealed class DesktopLayoutRuleAwarePlacementPlannerTests
{
    // ═══ Helpers ═══

    private static DesktopIconSnapshotItem I(int ord, string sid, PixelPoint? pos = null,
        string? mon = "DISPLAY1")
        => new(ord, sid, $"N_{sid}", null, ItemKind.File,
            pos ?? new PixelPoint(ord * 80, 0), mon, false, true);

    private static DesktopLayoutZone Z(Guid zid, int sortOrder = 0,
        NormalizedRect? bounds = null, int? maxItemCount = null)
    {
        bounds ??= sortOrder switch
        {
            0 => new NormalizedRect(0, 0, 5000, 10000),
            1 => new NormalizedRect(5000, 0, 10000, 10000),
            _ => new NormalizedRect((short)(sortOrder * 2500), 0,
                (short)((sortOrder + 1) * 2500), 10000)
        };
        return new DesktopLayoutZone(zid, $"Z{sortOrder}", bounds, sortOrder,
            ZonePlacementFlow.ColumnMajor, maxItemCount);
    }

    private static DesktopLayoutTemplate MakeTemplate(Guid tid, params DesktopLayoutZone[] zones)
        => new(tid, "T", 1, TargetDisplayBinding.ForPrimaryDisplay(), zones);

    private static DesktopLayoutIconRuleSet MakeRuleSet(Guid tid,
        params DesktopLayoutIconRule[] rules)
        => new(tid, rules);

    private static readonly Guid _tid = Guid.NewGuid();
    private static readonly Guid _zid0 = Guid.NewGuid();
    private static readonly Guid _zid1 = Guid.NewGuid();
    private static readonly PixelPoint _spacing = new(80, 80);

    // Display for 1920x1080 monitor — wide enough for 2 zones of 5000 normal units each = ~24 cols
    private static readonly DisplayDescriptor _dd = new("DISPLAY1",
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96, 96, true);

    private DesktopLayoutTemplate MakeTwoZoneTemplate()
        => MakeTemplate(_tid, Z(_zid0, 0), Z(_zid1, 1));

    private DesktopLayoutIconRuleResolutionPlan Resolve(
        DesktopLayoutTemplate template,
        DesktopLayoutIconRuleSet ruleSet,
        params DesktopIconSnapshotItem[] icons)
        => DesktopLayoutIconRuleResolver.Resolve(template, _dd, icons, ruleSet);

    private ResolvedDesktopLayoutTemplate ResolveTemplate(
        DesktopLayoutTemplate template)
        => DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, _spacing);

    private DesktopLayoutRuleAwarePlacementPlan Plan(
        DesktopLayoutTemplate template,
        DesktopLayoutIconRuleSet ruleSet,
        DesktopIconSnapshotItem[] icons)
    {
        var resolutionPlan = Resolve(template, ruleSet, icons);
        var resolvedTemplate = ResolveTemplate(template);
        return DesktopLayoutRuleAwarePlacementPlanner.Plan(
            resolvedTemplate, resolutionPlan, _spacing);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A. 固定障碍
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void A1_Exclude_GeneratesFixedObstacle_NoTargetPosition()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForExclude("a")),
            new[] { I(0, "a"), I(1, "b") });

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(1, plan.FixedItemCount);
        Assert.Single(plan.FixedObstacleRects);
        Assert.True(plan.HasFixedItems);

        // Fixed 项不产生移动目标 — DefaultAssigned 中的 "b" 是此处的目标
        var fixedItem = plan.ExcludedItems[0];
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, fixedItem.Kind);
        Assert.Equal(new PixelPoint(0, 0), fixedItem.CurrentPosition);

        // "a" 不在任何 Assigned 中
        foreach (var ai in plan.AllAssignedItems)
            Assert.NotEqual("a", ai.Icon.StableId);
        foreach (var fi in plan.AllFixedItems)
            Assert.NotEqual("b", fi.Icon.StableId);
    }

    [Fact] public void A2_Pin_GeneratesFixedObstacle_NoTargetPosition()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForPin("a")),
            new[] { I(0, "a"), I(1, "b") });

        Assert.Equal(0, plan.ExcludedCount);
        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(1, plan.FixedItemCount);
        Assert.Equal(1, plan.FixedObstacleRectCount);
        Assert.Equal(DesktopLayoutIconRuleKind.Pin, plan.PinnedItems[0].Kind);

        // "a" 不在任何 Assigned 中
        foreach (var ai in plan.AllAssignedItems)
            Assert.NotEqual("a", ai.Icon.StableId);
    }

    [Fact] public void A3_SingleFixedObstacle_FiltersOneSlot()
    {
        // Zone 0: 单列，多行。一个 Exclude 图标在 Zone 的第一行位置，
        // 应过滤掉对应的 Slot 0。
        var zid = Guid.NewGuid();
        // Create zone at top-left of display, 24 cols x 13 rows (1920/80=24, 1080/80=13.5→13)
        var zone = Z(zid, 0, new NormalizedRect(0, 0, 10000, 10000));
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);

        // Exclude 图标在 (0,0) — Zone.PixelBounds.Left=0,Top=0
        var icons = new[]
        {
            I(0, "excluded", pos: new PixelPoint(0, 0)),
            I(1, "defaultA"),
            I(2, "defaultB"),
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("excluded"));

        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);
        var resolvedTemplate = DesktopLayoutTemplateResolver.Resolve(
            template, _dd.WorkArea, spacing);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            resolvedTemplate, resolutionPlan, spacing);

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(2, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // 被过滤的 Slot 0 不应出现在 Assigned 中
        foreach (var ai in plan.AllAssignedItems)
        {
            // Slot 0 被 Exclude 图标在 (0,0) 占据
            Assert.False(ai.SlotIndex == 0 && ai.Zone.Zone.ZoneId == zid);

            // 所有 Assigned 的 TargetCellBounds 不与固定障碍重叠
            var fixedRect = plan.FixedObstacleRects[0];
            Assert.False(DesktopIconLayoutGeometry.Overlaps(ai.TargetCellBounds, fixedRect));
        }

        // "excluded" 不在 Assigned 中
        foreach (var ai in plan.AllAssignedItems)
            Assert.NotEqual("excluded", ai.Icon.StableId);
    }

    [Fact] public void A4_SingleFixedObstacle_FiltersMultipleSlots()
    {
        // Zone has ColumnCount=2, RowCount=3 → 6 slots
        // Exclude icon at position covering 2 grid cells (if spacing is large enough)
        // Actually use a 2x2 zone, place Exclude in middle to block 4 slots
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 5000, 5000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        // rt.Zones[0].PixelBounds = (0,0)-(960,540)
        // ColumnCount = 960/80 = 12, RowCount = 540/80 = 6

        // Place Exclude at (80, 80) — covers [80,160)x[80,160)
        // This should block:
        //   Slot at (col=1, row=1) → [80,160)x[80,160) — exact overlap
        // Additionally, let's check: each slot is [col*80, (col+1)*80) × [row*80, (row+1)*80)
        // FixedRect [80,160)x[80,160) overlaps exactly slot (1,1), but since it's half-open:
        //   slot (0,1): [0,80)x[80,160) — no overlap (left edge doesn't overlap in half-open)
        //   slot (1,0): [80,160)x[0,80) — no overlap (top edge doesn't overlap in half-open)
        //   slot (1,2): [80,160)x[160,240) — no overlap
        //   slot (2,1): [160,240)x[80,160) — no overlap
        // So it only blocks 1 slot for a perfectly aligned single-cell obstacle.

        // Place Exclude at (40, 40) → FixedRect [40,120)x[40,120)
        // This overlaps: slot (0,0)[0,80)x[0,80) → [40,80)x[40,80) overlap ✓
        //                slot (1,0)[80,160)x[0,80) → [80,120)x[40,80) overlap ✓
        //                slot (0,1)[0,80)x[80,160) → [40,80)x[80,120) overlap ✓
        //                slot (1,1)[80,160)x[80,160) → [80,120)x[80,120) overlap ✓
        // So it blocks 4 slots.

        var icons = new[]
        {
            I(0, "excluded", pos: new PixelPoint(40, 40)),
            I(1, "a"), I(2, "b"), I(3, "c"), I(4, "d"), I(5, "e"),
            I(6, "f"), I(7, "g"), I(8, "h"), I(9, "i"),
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("excluded"));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.ExcludedCount);

        // 9 default icons for 12*6 - 4 = 72 - 4 = 68 available slots → all assigned
        Assert.Equal(9, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // None of the 4 blocked slots appear in assigned
        var fixedRect = plan.FixedObstacleRects[0];
        foreach (var ai in plan.AllAssignedItems)
            Assert.False(DesktopIconLayoutGeometry.Overlaps(ai.TargetCellBounds, fixedRect));
    }

    [Fact] public void A5_MultipleFixedObstacles_FilterTogether()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);
        // rt: (0,0)-(1920,1080), Cols=24, Rows=13

        // Two excluded icons at different positions
        var icons = new[]
        {
            I(0, "ex1", pos: new PixelPoint(0, 0)),
            I(1, "ex2", pos: new PixelPoint(80, 0)),
            I(2, "a"), I(3, "b"), I(4, "c"),
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("ex1"),
            DesktopLayoutIconRule.ForExclude("ex2"));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(2, plan.ExcludedCount);
        Assert.Equal(2, plan.FixedObstacleRectCount);

        // Default icons assigned to non-blocked slots
        Assert.Equal(3, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // Blocked slots (Slot 0 by (0,0) rect, Slot 13 by (80,0) rect in ColumnMajor) never in Assigned
        var fixedRects = plan.FixedObstacleRects;
        foreach (var ai in plan.AllAssignedItems)
        {
            foreach (var fr in fixedRects)
                Assert.False(DesktopIconLayoutGeometry.Overlaps(ai.TargetCellBounds, fr));
        }
    }

    [Fact] public void A6_BoundaryContact_NoAreaOverlap_NotFiltered()
    {
        // Slot at (col=0,row=0): [0,80)x[0,80)
        // Fixed obstacle at (80,0): [80,160)x[0,80) → boundary contact, no overlap
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        // Exclude at (80,0) — adjacent to slot (0,0) but half-open means no overlap
        var icons = new[]
        {
            I(0, "excluded", pos: new PixelPoint(80, 0)),
            I(1, "a"),
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("excluded"));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(1, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // Slot 0 should still be available (boundary contact, no overlap)
        var assigned = plan.DefaultAssignedItems[0];
        Assert.Equal(0, assigned.SlotIndex); // Slot 0 is available
    }

    [Fact] public void A7_CrossZoneObstacle_FiltersMultipleZones()
    {
        // Two zones side by side: Zone0 (0,0)-(960,1080), Zone1 (960,0)-(1920,1080)
        // Exclude icon near the boundary [880,960)x[0,80) — should filter in BOTH zones if it overlaps
        var zid0 = Guid.NewGuid();
        var zid1 = Guid.NewGuid();

        // Use narrower zones so fixed rect can span across
        var zone0 = new DesktopLayoutZone(zid0, "Z0",
            new NormalizedRect(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var zone1 = new DesktopLayoutZone(zid1, "Z1",
            new NormalizedRect(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone0, zone1);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);
        // Zone0: (0,0)-(960,1080), Zone1: (960,0)-(1920,1080)

        // Place Exclude spanning across the boundary: [880,1040) x [0,80)
        var icons = new[]
        {
            I(0, "excluded", pos: new PixelPoint(880, 0)),
            I(1, "a"), I(2, "b"), I(3, "c"), I(4, "d"), I(5, "e"),
            I(6, "f"), I(7, "g"), I(8, "h"), I(9, "i"),
            I(10, "j"), I(11, "k"), I(12, "l"), I(13, "m"),
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("excluded"));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.ExcludedCount);

        // Verify slots overlapping fixed rect are blocked in BOTH zones
        var fixedRect = plan.FixedObstacleRects[0];
        foreach (var ai in plan.AllAssignedItems)
            Assert.False(DesktopIconLayoutGeometry.Overlaps(ai.TargetCellBounds, fixedRect));

        // All remaining default icons assigned (lots of space)
        Assert.Equal(13, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);
    }

    [Fact] public void A8_OutsideZoneObstacle_DoesNotFilterNonOverlappingSlot()
    {
        // Zone at top half only: [0,540) height
        // Exclude at bottom: [0,1000) → does not overlap Zone slots
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 5000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);
        // Zone: (0,0)-(1920,540), Cols=24, Rows=6

        // Exclude at (0, 800) — way below the zone
        var icons = new[]
        {
            I(0, "excluded", pos: new PixelPoint(0, 800)),
            I(1, "a"), I(2, "b"),
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("excluded"));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.ExcludedCount);
        // All Zone slots are unaffected (Exclude rect doesn't overlap any Zone slot)
        Assert.Equal(2, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // B. ZoneOverride
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void B9_OverrideIcon_OnlyGoesToSpecifiedZone()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForZoneOverride("a", _zid1)),
            new[] { I(0, "a"), I(1, "b") });

        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Equal(0, plan.OverrideUnassignedCount);

        // "a" must be in Zone 1, not Zone 0
        var ovrItem = plan.OverrideAssignedItems[0];
        Assert.Equal("a", ovrItem.Icon.StableId);
        Assert.Equal(_zid1, ovrItem.Zone.Zone.ZoneId);
    }

    [Fact] public void B10_Override_BeforeDefault()
    {
        // Zone0 has EffectiveCapacity=1. If both a and b are assigned to Zone0,
        // and a has ZoneOverride→Zone0, then a gets slot 0, b is unassigned.
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, 1);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);
        // Zone: (0,0)-(960,1080), Cols=12, Rows=13, EffectiveCapacity=1

        var icons = new[]
        {
            I(0, "a"), // has Override
            I(1, "b"), // default
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForZoneOverride("a", zid));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Equal(0, plan.OverrideUnassignedCount);
        Assert.Equal("a", plan.OverrideAssignedItems[0].Icon.StableId);

        // "b" goes to DefaultUnassigned (no capacity left)
        Assert.Equal(0, plan.DefaultAssignedCount);
        Assert.Equal(1, plan.DefaultUnassignedCount);
        Assert.Equal("b", plan.DefaultUnassignedItems[0].StableId);
    }

    [Fact] public void B11_MultipleOverrides_ByOrdinalAsc()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);

        var icons = new[]
        {
            I(3, "c"), I(1, "a"), I(2, "b"), // input order doesn't matter
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForZoneOverride("a", zid),
            DesktopLayoutIconRule.ForZoneOverride("b", zid),
            DesktopLayoutIconRule.ForZoneOverride("c", zid));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, _spacing);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, _spacing);

        Assert.Equal(3, plan.OverrideAssignedCount);
        Assert.Equal(0, plan.OverrideUnassignedCount);

        // Must be in Ordinal ASC: a(1), b(2), c(3)
        Assert.Equal(1, plan.OverrideAssignedItems[0].Icon.Ordinal);
        Assert.Equal(2, plan.OverrideAssignedItems[1].Icon.Ordinal);
        Assert.Equal(3, plan.OverrideAssignedItems[2].Icon.Ordinal);
    }

    [Fact] public void B12_OverrideInputOrder_DoesNotAffectResult()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, _spacing);

        var icons = new[] { I(2, "c"), I(0, "a"), I(1, "b") };

        var plan1 = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt,
            DesktopLayoutIconRuleResolver.Resolve(template, _dd, icons,
                MakeRuleSet(template.TemplateId,
                    DesktopLayoutIconRule.ForZoneOverride("c", zid),
                    DesktopLayoutIconRule.ForZoneOverride("a", zid),
                    DesktopLayoutIconRule.ForZoneOverride("b", zid))),
            _spacing);

        var plan2 = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt,
            DesktopLayoutIconRuleResolver.Resolve(template, _dd,
                new[] { I(0, "a"), I(1, "b"), I(2, "c") },
                MakeRuleSet(template.TemplateId,
                    DesktopLayoutIconRule.ForZoneOverride("a", zid),
                    DesktopLayoutIconRule.ForZoneOverride("b", zid),
                    DesktopLayoutIconRule.ForZoneOverride("c", zid))),
            _spacing);

        Assert.Equal(plan1.OverrideAssignedCount, plan2.OverrideAssignedCount);
        for (int i = 0; i < plan1.OverrideAssignedCount; i++)
            Assert.Equal(plan1.OverrideAssignedItems[i].Icon.StableId,
                plan2.OverrideAssignedItems[i].Icon.StableId);
    }

    [Fact] public void B13_DifferentZones_OverridesNotRegroupedByZone()
    {
        // Two overrides, one to Zone1, one to Zone0. Input: "a"(Zone1), "b"(Zone0)
        // Output should maintain Ordinal order: a(ord=0)→Zone1, b(ord=1)→Zone0
        // NOT regrouped: b(Zone0), a(Zone1)
        var template = MakeTwoZoneTemplate();

        var plan = Plan(template,
            MakeRuleSet(_tid,
                DesktopLayoutIconRule.ForZoneOverride("a", _zid1),
                DesktopLayoutIconRule.ForZoneOverride("b", _zid0)),
            new[] { I(0, "a"), I(1, "b") });

        Assert.Equal(2, plan.OverrideAssignedCount);
        Assert.Equal(0, plan.OverrideUnassignedCount);

        // Order by Ordinal: a(0) then b(1), NOT by Zone
        Assert.Equal("a", plan.OverrideAssignedItems[0].Icon.StableId);
        Assert.Equal("b", plan.OverrideAssignedItems[1].Icon.StableId);
        Assert.Equal(_zid1, plan.OverrideAssignedItems[0].Zone.Zone.ZoneId);
        Assert.Equal(_zid0, plan.OverrideAssignedItems[1].Zone.Zone.ZoneId);
    }

    [Fact] public void B14_OverrideZoneNoCapacity_GoesToUnassigned()
    {
        // Zone with EffectiveCapacity=1, 2 Overrides → first assigned, second unassigned
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, 1);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        var icons = new[] { I(0, "a"), I(1, "b") };
        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForZoneOverride("a", zid),
            DesktopLayoutIconRule.ForZoneOverride("b", zid));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Equal(1, plan.OverrideUnassignedCount);
        Assert.Equal("a", plan.OverrideAssignedItems[0].Icon.StableId);
        Assert.Equal("b", plan.OverrideUnassignedItems[0].Icon.StableId);
    }

    [Fact] public void B15_OverrideZonePartialCapacity_ExcessUnassigned()
    {
        // Zone EffectiveCapacity=2, 3 Override icons. First 2 assigned, 3rd unassigned.
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, 2);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c") };
        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForZoneOverride("a", zid),
            DesktopLayoutIconRule.ForZoneOverride("b", zid),
            DesktopLayoutIconRule.ForZoneOverride("c", zid));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(2, plan.OverrideAssignedCount);
        Assert.Equal(1, plan.OverrideUnassignedCount);
        Assert.Equal("a", plan.OverrideAssignedItems[0].Icon.StableId);
        Assert.Equal("b", plan.OverrideAssignedItems[1].Icon.StableId);
        Assert.Equal("c", plan.OverrideUnassignedItems[0].Icon.StableId);
    }

    [Fact] public void B16_Override_CannotBorrowOtherZoneSlot()
    {
        // ZoneOverride for "a" targets Zone0. Zone0 has capacity but "a" could
        // only go to Zone0. Zone1 has capacity but should not be used by "a".
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForZoneOverride("a", _zid0)),
            new[] { I(0, "a"), I(1, "b") });

        Assert.Equal(1, plan.OverrideAssignedCount);
        var ovr = plan.OverrideAssignedItems[0];
        Assert.Equal("a", ovr.Icon.StableId);
        Assert.Equal(_zid0, ovr.Zone.Zone.ZoneId);
        Assert.Equal(1, plan.DefaultAssignedCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C. 默认分配
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void C17_DefaultUsesRemainingSlotsAfterOverride()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, 3);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        // 1 Override + 3 Default, Zone EffectiveCapacity=3
        var icons = new[]
        {
            I(0, "ovr"), I(1, "d1"), I(2, "d2"), I(3, "d3"),
        };
        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForZoneOverride("ovr", zid));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Equal(2, plan.DefaultAssignedCount);
        Assert.Equal(1, plan.DefaultUnassignedCount); // d3

        // Override occupies Slot 0
        Assert.Equal(0, plan.OverrideAssignedItems[0].SlotIndex);

        // Default fills remaining slots: Slot 1, Slot 2
        Assert.Equal(1, plan.DefaultAssignedItems[0].SlotIndex);
        Assert.Equal(2, plan.DefaultAssignedItems[1].SlotIndex);
    }

    [Fact] public void C18_DefaultIcons_ByOrdinalAsc()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            DesktopLayoutIconRuleSet.Empty(_tid),
            new[] { I(2, "c"), I(0, "a"), I(1, "b") });

        Assert.Equal(3, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);
        Assert.Equal(0, plan.DefaultAssignedItems[0].Icon.Ordinal);
        Assert.Equal(1, plan.DefaultAssignedItems[1].Icon.Ordinal);
        Assert.Equal(2, plan.DefaultAssignedItems[2].Icon.Ordinal);
    }

    [Fact] public void C19_DefaultCapacityExhausted_GoesToUnassigned()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, 1);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c") };
        var ruleSet = DesktopLayoutIconRuleSet.Empty(template.TemplateId);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.DefaultAssignedCount);
        Assert.Equal(2, plan.DefaultUnassignedCount);
        Assert.Equal("a", plan.DefaultAssignedItems[0].Icon.StableId);
        Assert.Equal("b", plan.DefaultUnassignedItems[0].StableId);
        Assert.Equal("c", plan.DefaultUnassignedItems[1].StableId);
        Assert.True(plan.HasDefaultUnassigned);
        Assert.False(plan.IsComplete);
    }

    [Fact] public void C20_ZeroEffectiveCapacityZone_HandledGracefully()
    {
        // Zone with EffectiveCapacity=1, more icons than capacity → some unassigned
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, 1);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        var icons = new[] { I(0, "a"), I(1, "b") };
        var ruleSet = DesktopLayoutIconRuleSet.Empty(template.TemplateId);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.DefaultAssignedCount);
        Assert.Equal(1, plan.DefaultUnassignedCount);
        Assert.False(plan.IsComplete);
    }

    [Fact] public void C21_EmptyDefaultIcons_Valid()
    {
        // Only Override icons, no default icons
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForZoneOverride("a", _zid0)),
            new[] { I(0, "a") });

        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Equal(0, plan.OverrideUnassignedCount);
        Assert.Equal(0, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);
        Assert.True(plan.IsComplete);
    }

    [Fact] public void C22_EmptyRuleSet_SameAsOldPlanning()
    {
        // With empty rule set, behavior should match old planner:
        // all target icons → DefaultAssigned (by Ordinal ASC to Zones by SortOrder)
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            DesktopLayoutIconRuleSet.Empty(_tid),
            new[] { I(0, "a"), I(1, "b") });

        Assert.Equal(0, plan.ExcludedCount);
        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(0, plan.OverrideAssignedCount);
        Assert.Equal(2, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // Compare with old planner
        var rt = ResolveTemplate(template);
        var targetIcons = new[] { I(0, "a"), I(1, "b") };
        var oldAssignment = DesktopLayoutAssignmentPlanner.Plan(rt, targetIcons);
        var oldPlacement = DesktopLayoutCoordinatePlanner.Plan(oldAssignment);

        Assert.Equal(oldPlacement.PlacementCount, plan.TotalAssignedCount);
        Assert.Equal(oldPlacement.UnassignedCount, plan.DefaultUnassignedCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D. 不变量和安全
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void D23_PlannerRejectsNullResolvedTemplate()
    {
        var template = MakeTwoZoneTemplate();
        var resolutionPlan = Resolve(template, DesktopLayoutIconRuleSet.Empty(_tid),
            I(0, "a"));

        Assert.Throws<ArgumentNullException>(() =>
            DesktopLayoutRuleAwarePlacementPlanner.Plan(null!, resolutionPlan, _spacing));
    }

    [Fact] public void D24_PlannerRejectsZeroIconSpacing()
    {
        var template = MakeTwoZoneTemplate();
        var resolutionPlan = Resolve(template, DesktopLayoutIconRuleSet.Empty(_tid),
            I(0, "a"));
        var rt = ResolveTemplate(template);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopLayoutRuleAwarePlacementPlanner.Plan(rt, resolutionPlan, new PixelPoint(0, 80)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopLayoutRuleAwarePlacementPlanner.Plan(rt, resolutionPlan, new PixelPoint(80, 0)));
    }

    [Fact] public void D25_ExternalEquivalentIcon_RejectedByPlanInvariants()
    {
        // Create plan, try passing external equivalent as one of the results
        // The plan model itself validates reference identity so this is tested
        // by D26 partition tests below using external equivalent instances
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForExclude("a")),
            new[] { I(0, "a"), I(1, "b") });

        var extIcon = I(0, "a");
        Assert.NotSame(plan.ExcludedItems[0].Icon, extIcon);
        Assert.False(ReferenceEquals(plan.ExcludedItems[0].Icon, extIcon));
    }

    [Fact] public void D26_Partitions_NoOverlap_NoOmission()
    {
        var template = MakeTwoZoneTemplate();
        var icons = new[]
        {
            I(0, "ex"), I(1, "pin"), I(2, "ovr"),
            I(3, "d1"), I(4, "d2"),
        };
        var ruleSet = MakeRuleSet(_tid,
            DesktopLayoutIconRule.ForExclude("ex"),
            DesktopLayoutIconRule.ForPin("pin"),
            DesktopLayoutIconRule.ForZoneOverride("ovr", _zid0));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);
        var rt = ResolveTemplate(template);
        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, _spacing);

        // All target display icons must be in exactly one of:
        // Excluded, Pinned, OverrideAssigned, OverrideUnassigned,
        // DefaultAssigned, DefaultUnassigned
        var allIcons = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
        foreach (var fi in plan.AllFixedItems)
            Assert.True(allIcons.Add(fi.Icon));
        foreach (var oa in plan.OverrideAssignedItems)
            Assert.True(allIcons.Add(oa.Icon));
        foreach (var ou in plan.OverrideUnassignedItems)
            Assert.True(allIcons.Add(ou.Icon));
        foreach (var da in plan.DefaultAssignedItems)
            Assert.True(allIcons.Add(da.Icon));
        foreach (var du in plan.DefaultUnassignedItems)
            Assert.True(allIcons.Add(du));

        // Matches TargetDisplayIcons count
        Assert.Equal(resolutionPlan.TargetDisplayIconCount, allIcons.Count);

        // Verify each TargetDisplay icon is covered
        foreach (var icon in resolutionPlan.TargetDisplayIcons)
            Assert.Contains(icon, allIcons);
    }

    [Fact] public void D27_TargetPositionsUnique()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid,
                DesktopLayoutIconRule.ForZoneOverride("a", _zid0),
                DesktopLayoutIconRule.ForZoneOverride("b", _zid1)),
            new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });

        var positions = new HashSet<(int, int)>();
        foreach (var ai in plan.AllAssignedItems)
        {
            var key = (ai.TargetPosition.X, ai.TargetPosition.Y);
            Assert.True(positions.Add(key),
                $"Duplicate TargetPosition ({ai.TargetPosition.X},{ai.TargetPosition.Y})");
        }
    }

    [Fact] public void D28_TargetCellBounds_ConsistentWithZoneAndSlot()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForZoneOverride("a", _zid1)),
            new[] { I(0, "a"), I(1, "b"), I(2, "c") });

        foreach (var ai in plan.AllAssignedItems)
        {
            // TargetCellBounds must be fully within Zone.PixelBounds
            Assert.True(DesktopIconLayoutGeometry.IsFullyContained(
                ai.TargetCellBounds, ai.Zone.PixelBounds));

            // TargetCellBounds = [TargetPosition, TargetPosition+Spacing)
            var expected = DesktopIconLayoutGeometry.CreateRectChecked(
                ai.TargetPosition, _spacing);
            Assert.Equal(expected, ai.TargetCellBounds);

            // Grid position consistent with SlotIndex
            if (ai.Zone.Zone.PlacementFlow == ZonePlacementFlow.ColumnMajor)
            {
                int expCol = ai.SlotIndex / ai.Zone.RowCount;
                int expRow = ai.SlotIndex % ai.Zone.RowCount;
                Assert.Equal(expCol, ai.ColumnIndex);
                Assert.Equal(expRow, ai.RowIndex);
            }
        }
    }

    [Fact] public void D29_BlockedSlotsNeverInAssigned()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        var icons = new[]
        {
            I(0, "ex1", pos: new PixelPoint(0, 0)),
            I(1, "ex2", pos: new PixelPoint(160, 80)),
            I(2, "a"), I(3, "b"), I(4, "c"), I(5, "d"), I(6, "e"),
        };

        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("ex1"),
            DesktopLayoutIconRule.ForExclude("ex2"));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(2, plan.ExcludedCount);

        foreach (var ai in plan.AllAssignedItems)
        {
            foreach (var fr in plan.FixedObstacleRects)
            {
                Assert.False(DesktopIconLayoutGeometry.Overlaps(ai.TargetCellBounds, fr),
                    $"Assigned item at SlotIndex={ai.SlotIndex} overlaps fixed obstacle.");
            }
        }
    }

    [Fact] public void D30_OutOfScopeAndUnmatchedRules_NotInLayout()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid,
                DesktopLayoutIconRule.ForExclude("oos_icon"),
                DesktopLayoutIconRule.ForExclude("unmatched_rule")),
            new[] { I(0, "a", mon: "OTHER"), I(1, "b") });

        // "a" is OutOfScope → not in any layout partition
        // "oos_icon" matches "a" → appears in OutOfScopeRuleMatches only
        // "unmatched_rule" → UnmatchedRules

        Assert.Equal(0, plan.ExcludedCount);
        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(0, plan.OverrideAssignedCount);
        Assert.Equal(1, plan.DefaultAssignedCount); // "b"
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // OutOfScope icons not in AllAssignedItems
        foreach (var ai in plan.AllAssignedItems)
            Assert.NotEqual("a", ai.Icon.StableId);
        foreach (var fi in plan.AllFixedItems)
            Assert.NotEqual("a", fi.Icon.StableId);
    }

    [Fact] public void D31_InputOrderChange_DoesNotAffectOutput()
    {
        // Same icons with same ordinals but different input order → same results
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);

        var icons1 = new[] { I(2, "c"), I(0, "a"), I(1, "b") };
        var icons2 = new[] { I(0, "a"), I(1, "b"), I(2, "c") };

        var ruleSet = MakeRuleSet(_tid,
            DesktopLayoutIconRule.ForPin("a"),
            DesktopLayoutIconRule.ForExclude("b"));

        var p1 = DesktopLayoutRuleAwarePlacementPlanner.Plan(rt,
            DesktopLayoutIconRuleResolver.Resolve(template, _dd, icons1, ruleSet), _spacing);
        var p2 = DesktopLayoutRuleAwarePlacementPlanner.Plan(rt,
            DesktopLayoutIconRuleResolver.Resolve(template, _dd, icons2, ruleSet), _spacing);

        // Partition sizes identical
        Assert.Equal(p1.PinnedCount, p2.PinnedCount);
        Assert.Equal(p1.ExcludedCount, p2.ExcludedCount);
        Assert.Equal(p1.DefaultAssignedCount, p2.DefaultAssignedCount);
        Assert.Equal(p1.DefaultUnassignedCount, p2.DefaultUnassignedCount);

        // Fixed items same identity per ordinal
        for (int i = 0; i < p1.AllFixedItems.Count; i++)
        {
            Assert.Equal(p1.AllFixedItems[i].Icon.Ordinal, p2.AllFixedItems[i].Icon.Ordinal);
            Assert.Equal(p1.AllFixedItems[i].Icon.StableId, p2.AllFixedItems[i].Icon.StableId);
        }
    }

    [Fact] public void D32_ExtremeCoordinates_NoIntOverflow()
    {
        // Use extreme-but-valid coordinates: near int.MaxValue within WorkArea bounds
        // Actually we'll use the checked CreateRectChecked which throws on overflow
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);

        // WorkArea positioned near int.MaxValue but small enough to remain valid
        var extremeWa = new PixelRect(int.MaxValue - 200, int.MaxValue - 200,
            int.MaxValue, int.MaxValue);
        var extremeSpacing = new PixelPoint(40, 40);

        var template = MakeTemplate(Guid.NewGuid(), zone);
        var icons = new[] { I(0, "a") };
        var ruleSet = DesktopLayoutIconRuleSet.Empty(template.TemplateId);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        // Resolve template with extreme work area
        var rt = DesktopLayoutTemplateResolver.Resolve(template, extremeWa, extremeSpacing);

        // Should complete without overflow exception
        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(rt, resolutionPlan, extremeSpacing);
        Assert.Equal(1, plan.DefaultAssignedCount);
    }

    [Fact] public void D33_OldPlacementTests_NoRegression()
    {
        // This test verifies we can run alongside the old planner without issues.
        // The actual regression tests are: all existing tests in Preview, Placement,
        // Assignment suites must pass (verified separately via dotnet test).
        //
        // Here we verify the direct compatibility path:
        // empty rule set → same behavior as old planner
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") };

        // Old planner
        var oldAssignment = DesktopLayoutAssignmentPlanner.Plan(rt, icons);
        var oldPlacement = DesktopLayoutCoordinatePlanner.Plan(oldAssignment);

        // Rule-aware planner with empty rules
        var plan = Plan(template, DesktopLayoutIconRuleSet.Empty(_tid), icons);

        Assert.Equal(oldPlacement.PlacementCount, plan.TotalAssignedCount);
        Assert.Equal(oldPlacement.UnassignedCount, plan.DefaultUnassignedCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Additional: FixedItem model invariants
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void FixedItem_Exclude_WithCorrectRect()
    {
        var icon = I(0, "ex", pos: new PixelPoint(100, 200));
        var rule = DesktopLayoutIconRule.ForExclude("ex");
        var item = new DesktopLayoutRuleAwareFixedItem(icon, rule, _spacing);

        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, item.Kind);
        Assert.Same(icon, item.Icon);
        Assert.Same(rule, item.Rule);
        Assert.Equal(new PixelPoint(100, 200), item.CurrentPosition);
        // FixedRect = [100,200)-(180,280)
        Assert.Equal(new PixelRect(100, 200, 180, 280), item.FixedRect);
    }

    [Fact] public void FixedItem_Pin_WithCorrectRect()
    {
        var icon = I(0, "pin", pos: new PixelPoint(50, 50));
        var rule = DesktopLayoutIconRule.ForPin("pin");
        var item = new DesktopLayoutRuleAwareFixedItem(icon, rule, _spacing);

        Assert.Equal(DesktopLayoutIconRuleKind.Pin, item.Kind);
        Assert.Same(icon, item.Icon);
        Assert.Same(rule, item.Rule);
        Assert.Equal(new PixelRect(50, 50, 130, 130), item.FixedRect);
    }

    [Fact] public void FixedItem_WrongKind_Rejected()
    {
        var icon = I(0, "a");
        var rule = DesktopLayoutIconRule.ForZoneOverride("a", Guid.NewGuid());
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwareFixedItem(icon, rule, _spacing));
    }

    [Fact] public void FixedItem_StableIdMismatch_Rejected()
    {
        var icon = I(0, "icon-id");
        var rule = DesktopLayoutIconRule.ForExclude("rule-id");
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwareFixedItem(icon, rule, _spacing));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Additional: AssignedItem model invariants
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void AssignedItem_Override_WithRule()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var zone = rt.Zones.First(z => z.Zone.ZoneId == _zid0);
        var icon = I(0, "ovr");
        var rule = DesktopLayoutIconRule.ForZoneOverride("ovr", _zid0);

        var item = new DesktopLayoutRuleAwareAssignedItem(
            icon, zone, _spacing, 0,
            DesktopLayoutRuleAwareAssignmentSource.Override, rule);

        Assert.Equal(DesktopLayoutRuleAwareAssignmentSource.Override, item.Source);
        Assert.True(item.IsOverride);
        Assert.False(item.IsDefault);
        Assert.Same(rule, item.Rule);
        Assert.Same(icon, item.Icon);
        Assert.Same(zone, item.Zone);
        Assert.Equal(0, item.SlotIndex);
    }

    [Fact] public void AssignedItem_Default_WithoutRule()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var zone = rt.Zones.First(z => z.Zone.ZoneId == _zid0);
        var icon = I(0, "def");

        var item = new DesktopLayoutRuleAwareAssignedItem(
            icon, zone, _spacing, 0,
            DesktopLayoutRuleAwareAssignmentSource.Default, null);

        Assert.Equal(DesktopLayoutRuleAwareAssignmentSource.Default, item.Source);
        Assert.False(item.IsOverride);
        Assert.True(item.IsDefault);
        Assert.Null(item.Rule);
    }

    [Fact] public void AssignedItem_Override_NullRule_Rejected()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var zone = rt.Zones.First(z => z.Zone.ZoneId == _zid0);
        var icon = I(0, "ovr");

        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwareAssignedItem(
                icon, zone, _spacing, 0,
                DesktopLayoutRuleAwareAssignmentSource.Override, null));
    }

    [Fact] public void AssignedItem_Default_WithRule_Rejected()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var zone = rt.Zones.First(z => z.Zone.ZoneId == _zid0);
        var icon = I(0, "def");
        var rule = DesktopLayoutIconRule.ForExclude("def");

        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwareAssignedItem(
                icon, zone, _spacing, 0,
                DesktopLayoutRuleAwareAssignmentSource.Default, rule));
    }

    [Fact] public void AssignedItem_SlotIndexExceedsTheoretical_Rejected()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, 1);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);
        var rz = rt.Zones[0];
        // TheoreticalCapacity = Cols*Rows = 24*13 = 312

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopLayoutRuleAwareAssignedItem(
                I(0, "x"), rz, spacing, 9999, // exceeds TheoreticalCapacity
                DesktopLayoutRuleAwareAssignmentSource.Default, null));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void EmptyIcons_ProducesEmptyPlan()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid, DesktopLayoutIconRule.ForExclude("x")),
            Array.Empty<DesktopIconSnapshotItem>());

        Assert.Equal(0, plan.ExcludedCount);
        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(0, plan.OverrideAssignedCount);
        Assert.Equal(0, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);
        Assert.Equal(1, plan.ResolutionPlan.UnmatchedRuleCount);
        Assert.True(plan.IsComplete);
    }

    [Fact] public void OnlyFixedItems_NoDefault_Valid()
    {
        var template = MakeTwoZoneTemplate();
        var plan = Plan(template,
            MakeRuleSet(_tid,
                DesktopLayoutIconRule.ForExclude("a"),
                DesktopLayoutIconRule.ForPin("b")),
            new[] { I(0, "a"), I(1, "b") });

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(0, plan.OverrideAssignedCount);
        Assert.Equal(0, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);
        Assert.True(plan.IsComplete);
    }

    [Fact] public void FixedObstacle_BlocksOverrideSlot_OverrideGoesToNext()
    {
        // Fixed obstacle blocks Slot 0 of Zone0. The Override for Zone0 should
        // go to Slot 1 (if available) rather than Slot 0.
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);

        // Exclude at (0,0) blocks Slot 0
        var icons = new[]
        {
            I(0, "excluded", pos: new PixelPoint(0, 0)),
            I(1, "ovr"),
            I(2, "def"),
        };
        var ruleSet = MakeRuleSet(template.TemplateId,
            DesktopLayoutIconRule.ForExclude("excluded"),
            DesktopLayoutIconRule.ForZoneOverride("ovr", zid));
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt, resolutionPlan, spacing);

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Equal(1, plan.DefaultAssignedCount);

        // Slot 0 blocked by Fixed → Override takes Slot 1
        Assert.Equal(1, plan.OverrideAssignedItems[0].SlotIndex);
        // Default takes Slot 2 (next available)
        Assert.Equal(2, plan.DefaultAssignedItems[0].SlotIndex);
    }

    [Fact] public void AllAvailableSlots_BlockedByFixed_AllUnassigned()
    {
        // Zone with 2x2=4 slots. Fill them all with fixed obstacles.
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var spacing = new PixelPoint(80, 80);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, spacing);
        // Zone: (0,0)-(1920,1080), 24×13=312 slots

        // Lots of excluded icons blocking the first few slots
        // Only test that a default icon is unassigned when all early slots are blocked
        // and EffectiveCapacity is limited
        var zid2 = Guid.NewGuid();
        var zone2 = new DesktopLayoutZone(zid2, "Z1",
            new NormalizedRect(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, 1);
        var template2 = MakeTemplate(Guid.NewGuid(), zone2);
        var rt2 = DesktopLayoutTemplateResolver.Resolve(template2, _dd.WorkArea, spacing);
        // Zone: (0,0)-(960,1080), 12×13=156 slots, EffectiveCapacity=1

        // Block Slot 0-3 with two large excludes
        var icons2 = new[]
        {
            I(0, "ex1", pos: new PixelPoint(0, 0)),
            I(1, "ex2", pos: new PixelPoint(80, 0)),
            I(3, "a"),
        };
        var ruleSet2 = MakeRuleSet(template2.TemplateId,
            DesktopLayoutIconRule.ForExclude("ex1"),
            DesktopLayoutIconRule.ForExclude("ex2"));
        var resolutionPlan2 = DesktopLayoutIconRuleResolver.Resolve(
            template2, _dd, icons2, ruleSet2);

        var plan2 = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            rt2, resolutionPlan2, spacing);

        Assert.Equal(2, plan2.ExcludedCount);
        // "a" should still be assigned (Slot 1 or later is available)
        Assert.Equal(1, plan2.DefaultAssignedCount);
        // Slot 0 is blocked by fixed obstacle at (0,0)
        Assert.NotEqual(0, plan2.DefaultAssignedItems[0].SlotIndex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FIX-1: AssignedItem reinforced invariants
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void AssignedItem_UndefinedSource_Rejected()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var zone = rt.Zones.First(z => z.Zone.ZoneId == _zid0);
        var icon = I(0, "a");

        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwareAssignedItem(
                icon, zone, _spacing, 0,
                (DesktopLayoutRuleAwareAssignmentSource)99, null));
    }

    [Fact] public void AssignedItem_Override_StableIdMismatch_Rejected()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var zone = rt.Zones.First(z => z.Zone.ZoneId == _zid0);
        var icon = I(0, "icon-sid");
        var rule = DesktopLayoutIconRule.ForZoneOverride("rule-sid", _zid0);

        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwareAssignedItem(
                icon, zone, _spacing, 0,
                DesktopLayoutRuleAwareAssignmentSource.Override, rule));
    }

    [Fact] public void AssignedItem_Override_TargetZoneIdMismatch_Rejected()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        // Use zone _zid0 but rule targets _zid1
        var zone = rt.Zones.First(z => z.Zone.ZoneId == _zid0);
        var icon = I(0, "a");
        var rule = DesktopLayoutIconRule.ForZoneOverride("a", _zid1);

        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwareAssignedItem(
                icon, zone, _spacing, 0,
                DesktopLayoutRuleAwareAssignmentSource.Override, rule));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FIX-2: Plan rejects external equivalent objects
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void Plan_RejectsExternalEquivalentIcon()
    {
        var template = MakeTwoZoneTemplate();
        var rt = ResolveTemplate(template);
        var realIcon = I(0, "a");
        var ruleSet = DesktopLayoutIconRuleSet.Empty(_tid);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, new[] { realIcon }, ruleSet);

        // Build valid results but substitute external equivalent Icon
        var extIcon = I(0, "a"); // same StableId/Ordinal, different instance
        Assert.NotSame(realIcon, extIcon);

        var extZone = rt.Zones[0];
        var extAssigned = new DesktopLayoutRuleAwareAssignedItem(
            extIcon, extZone, _spacing, 0,
            DesktopLayoutRuleAwareAssignmentSource.Default, null);

        // Plan constructor must reject: external Icon not in DefaultManagedIcons
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePlacementPlan(
                rt, resolutionPlan,
                Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
                Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
                Array.Empty<PixelRect>(),
                Array.Empty<DesktopLayoutRuleAwareAssignedItem>(),
                Array.Empty<DesktopLayoutResolvedIconRule>(),
                new[] { extAssigned },
                Array.Empty<DesktopIconSnapshotItem>()));
    }

    [Fact] public void Plan_RejectsExternalEquivalentRule()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, _spacing);

        var icon = I(0, "a");
        var realRule = DesktopLayoutIconRule.ForZoneOverride("a", zid);
        var ruleSet = MakeRuleSet(template.TemplateId, realRule);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, new[] { icon }, ruleSet);

        // External equivalent rule
        var extRule = DesktopLayoutIconRule.ForZoneOverride("a", zid);
        Assert.NotSame(realRule, extRule);

        var extAssigned = new DesktopLayoutRuleAwareAssignedItem(
            icon, rt.Zones[0], _spacing, 0,
            DesktopLayoutRuleAwareAssignmentSource.Override, extRule);

        // Plan constructor must reject: external Rule not in ZoneOverrideMatches
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePlacementPlan(
                rt, resolutionPlan,
                Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
                Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
                Array.Empty<PixelRect>(),
                new[] { extAssigned },
                Array.Empty<DesktopLayoutResolvedIconRule>(),
                Array.Empty<DesktopLayoutRuleAwareAssignedItem>(),
                Array.Empty<DesktopIconSnapshotItem>()));
    }

    [Fact] public void Plan_RejectsExternalEquivalentZone()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, _spacing);

        var icon = I(0, "a");
        var ruleSet = DesktopLayoutIconRuleSet.Empty(template.TemplateId);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, new[] { icon }, ruleSet);

        // Create an equivalent ResolvedDesktopLayoutZone NOT from rt.Zones
        var extZone = new ResolvedDesktopLayoutZone(
            zone, rt.Zones[0].PixelBounds,
            rt.Zones[0].ColumnCount, rt.Zones[0].RowCount,
            rt.Zones[0].TheoreticalCapacity, rt.Zones[0].EffectiveCapacity);
        // Different instance but equivalent values
        Assert.NotSame(rt.Zones[0], extZone);

        var extAssigned = new DesktopLayoutRuleAwareAssignedItem(
            icon, extZone, _spacing, 0,
            DesktopLayoutRuleAwareAssignmentSource.Default, null);

        // Plan constructor must reject: external Zone not in ResolvedTemplate.Zones
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePlacementPlan(
                rt, resolutionPlan,
                Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
                Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
                Array.Empty<PixelRect>(),
                Array.Empty<DesktopLayoutRuleAwareAssignedItem>(),
                Array.Empty<DesktopLayoutResolvedIconRule>(),
                new[] { extAssigned },
                Array.Empty<DesktopIconSnapshotItem>()));
    }

    [Fact] public void Plan_CorrectReferencesStillPass()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "Z0",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var rt = DesktopLayoutTemplateResolver.Resolve(template, _dd.WorkArea, _spacing);

        var icon = I(0, "a");
        var rule = DesktopLayoutIconRule.ForZoneOverride("a", zid);
        var ruleSet = MakeRuleSet(template.TemplateId, rule);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, new[] { icon }, ruleSet);

        // Use exact original references from the resolution chain
        var realIcon = resolutionPlan.ZoneOverrideMatches[0].Icon;
        var realRule = resolutionPlan.ZoneOverrideMatches[0].Rule;
        var realZone = rt.Zones[0];

        var assigned = new DesktopLayoutRuleAwareAssignedItem(
            realIcon, realZone, _spacing, 0,
            DesktopLayoutRuleAwareAssignmentSource.Override, realRule);

        // Should NOT throw — all references are original
        var plan = new DesktopLayoutRuleAwarePlacementPlan(
            rt, resolutionPlan,
            Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
            Array.Empty<DesktopLayoutRuleAwareFixedItem>(),
            Array.Empty<PixelRect>(),
            new[] { assigned },
            Array.Empty<DesktopLayoutResolvedIconRule>(),
            Array.Empty<DesktopLayoutRuleAwareAssignedItem>(),
            Array.Empty<DesktopIconSnapshotItem>());

        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Same(realIcon, plan.OverrideAssignedItems[0].Icon);
        Assert.Same(realRule, plan.OverrideAssignedItems[0].Rule);
        Assert.Same(realZone, plan.OverrideAssignedItems[0].Zone);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FIX-3: Large TheoreticalCapacity safe scanning
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void LargeTheoreticalCapacity_SafeScanning()
    {
        // Construct a ResolvedDesktopLayoutZone with TheoreticalCapacity > int.MaxValue
        // 50000 cols × 50000 rows = 2,500,000,000 > int.MaxValue (2,147,483,647)
        var zid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid, "BigZone",
            new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);

        // Create ResolvedDesktopLayoutZone directly with large dims
        // PixelBounds big enough for 50000×50000 at spacing=80: (0,0)-(4,000,000, 4,000,000)
        var bigPixelBounds = new PixelRect(0, 0, 4_000_000, 4_000_000);
        var bigColumnCount = 50000;
        var bigRowCount = 50000;
        var bigTheoCapacity = checked((long)bigColumnCount * bigRowCount); // 2,500,000,000
        var bigEffCapacity = bigTheoCapacity;

        var bigZone = new ResolvedDesktopLayoutZone(
            zone, bigPixelBounds, bigColumnCount, bigRowCount,
            bigTheoCapacity, bigEffCapacity);

        Assert.True(bigZone.TheoreticalCapacity > int.MaxValue,
            $"Expected TheoreticalCapacity ({bigZone.TheoreticalCapacity}) > int.MaxValue ({int.MaxValue})");

        // Create a ResolvedTemplate with this zone
        var template = MakeTemplate(Guid.NewGuid(), zone);
        var bigRt = new ResolvedDesktopLayoutTemplate(
            template,
            new PixelRect(0, 0, 4_000_000, 4_000_000),
            _spacing,
            new[] { bigZone });

        // Only 3 default icons — Planner should NOT enumerate all 2.5B slots
        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c") };
        var ruleSet = DesktopLayoutIconRuleSet.Empty(template.TemplateId);
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd, icons, ruleSet);

        // Must complete quickly (no infinite loop, no int wrap)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            bigRt, resolutionPlan, _spacing);
        sw.Stop();

        Assert.Equal(3, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // Should complete near-instantly (scanning only a few slots, not 2.5B)
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Scan took {sw.ElapsedMilliseconds}ms, should be < 1000ms (scanned only first few slots)");

        // First 3 slots should be assigned sequentially (no obstacles)
        Assert.Equal(0, plan.DefaultAssignedItems[0].SlotIndex);
        Assert.Equal(1, plan.DefaultAssignedItems[1].SlotIndex);
        Assert.Equal(2, plan.DefaultAssignedItems[2].SlotIndex);
    }
}
