using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Preview;

/// <summary>
/// 槽位分配只由「规则优先 + Icon.Ordinal」决定 —— 图标一旦在枚举顺序里插队，
/// 其后每个受管图标的槽位都会平移一格。真机上观察到的「面板预览 0 项 vs 引擎建议 17 项」
/// 分歧就来自这里：同一份模板、同一批位置，新图标排在枚举顺序最前时全部要动，
/// 排在最后时一步都不用动。
/// </summary>
public sealed class DesktopLayoutRuleAwarePreviewPlannerOrderSensitivityTests
{
    private static readonly PixelPoint Spacing = new(80, 80);

    private static readonly DisplayDescriptor Display = new("DISPLAY1",
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96, 96, true);

    // 80×80 间距、列优先流下的前 6 个槽位中心。
    private static readonly PixelPoint[] Cells =
    {
        new(0, 0), new(0, 80), new(0, 160), new(0, 240), new(0, 320), new(0, 400),
    };

    private static DesktopIconSnapshotItem Icon(int ordinal, string stableId, PixelPoint position)
        => new(ordinal, stableId, $"N_{stableId}", null, ItemKind.File, position, "DISPLAY1", false, true);

    private static DesktopLayoutTemplate Template(Guid templateId, Guid zoneId)
        => new(templateId, "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zoneId, "Z0",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });

    private static DesktopLayoutRuleAwarePreviewPlan Plan(
        DesktopLayoutTemplate template,
        IReadOnlyList<DesktopIconSnapshotItem> icons,
        bool stable = false,
        DesktopLayoutIconRuleSet? rules = null,
        IReadOnlyList<DisplayDescriptor>? displays = null,
        IReadOnlySet<string>? explicitInboundStableIds = null)
        => DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            rules ?? DesktopLayoutIconRuleSet.Empty(template.TemplateId),
            displays ?? [Display],
            null,
            Spacing,
            icons,
            explicitInboundStableIds,
            preserveExistingGridSlots: stable);

    [Fact]
    public void FiveIconsSittingExactlyOnTheirCells_RequireNoMove()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        var icons = new[]
        {
            Icon(0, "a", Cells[0]),
            Icon(1, "b", Cells[1]),
            Icon(2, "c", Cells[2]),
            Icon(3, "d", Cells[3]),
            Icon(4, "e", Cells[4]),
        };

        var plan = Plan(template, icons);

        Assert.Equal(5, plan.AlreadyAtTargetCount);
        Assert.Equal(0, plan.MoveRequiredCount);
    }

    [Fact]
    public void NewIconInsertedFirstInEnumerationOrder_ShiftsEveryManagedIcon()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        // 新图标排在枚举顺序最前（Ordinal 0），既有 5 个图标的 Ordinal 各 +1，位置一个都没变。
        var icons = new[]
        {
            Icon(0, "new", Cells[5]),
            Icon(1, "a", Cells[0]),
            Icon(2, "b", Cells[1]),
            Icon(3, "c", Cells[2]),
            Icon(4, "d", Cells[3]),
            Icon(5, "e", Cells[4]),
        };

        var plan = Plan(template, icons);

        Assert.Equal(0, plan.AlreadyAtTargetCount);
        Assert.Equal(6, plan.MoveRequiredCount);
    }

    [Fact]
    public void NewIconAppendedLastInEnumerationOrder_RequiresNoMove()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        // 同一个新图标、同一个位置，只是它在枚举顺序里排在最后。
        var icons = new[]
        {
            Icon(0, "a", Cells[0]),
            Icon(1, "b", Cells[1]),
            Icon(2, "c", Cells[2]),
            Icon(3, "d", Cells[3]),
            Icon(4, "e", Cells[4]),
            Icon(5, "new", Cells[5]),
        };

        var plan = Plan(template, icons);

        Assert.Equal(6, plan.AlreadyAtTargetCount);
        Assert.Equal(0, plan.MoveRequiredCount);
    }
    [Fact]
    public void StableCandidate_NewIconInsertedFirst_DoesNotShuffleExistingGrid()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        var icons = new[]
        {
            Icon(0, "new", Cells[5]),
            Icon(1, "a", Cells[0]),
            Icon(2, "b", Cells[1]),
            Icon(3, "c", Cells[2]),
            Icon(4, "d", Cells[3]),
            Icon(5, "e", Cells[4]),
        };

        var plan = Plan(template, icons, stable: true);

        Assert.Equal(6, plan.AlreadyAtTargetCount);
        Assert.Equal(0, plan.MoveRequiredCount);
        Assert.Equal(6, plan.Items.Select(item => item.TargetPosition).Distinct().Count());
    }

    [Fact]
    public void StableCandidate_NewOutOfOrderIcon_MovesOnlyTheNewIcon()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        var icons = new[]
        {
            Icon(0, "new", new PixelPoint(17, 480)),
            Icon(1, "a", Cells[0]),
            Icon(2, "b", Cells[1]),
            Icon(3, "c", Cells[2]),
            Icon(4, "d", Cells[3]),
            Icon(5, "e", Cells[4]),
        };

        var plan = Plan(template, icons, stable: true);

        Assert.Equal(5, plan.AlreadyAtTargetCount);
        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.Equal("new", Assert.Single(plan.Items, item => item.RequiresMove).Icon.StableId);
    }

    [Fact]
    public void StableCandidate_ExplicitZoneOverrideTakesPriorityOverDefaultPosition()
    {
        var zoneId = Guid.NewGuid();
        var template = Template(Guid.NewGuid(), zoneId);
        var rules = new DesktopLayoutIconRuleSet(template.TemplateId, [
            DesktopLayoutIconRule.ForZoneOverride("b", zoneId),
        ]);
        var icons = new[]
        {
            Icon(0, "a", Cells[1]),
            Icon(1, "b", Cells[1]),
        };

        var plan = Plan(template, icons, stable: true, rules: rules);

        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.Equal(Cells[1], plan.Items.Single(item => item.Icon.StableId == "b").TargetPosition);
        Assert.Equal(Cells[0], plan.Items.Single(item => item.Icon.StableId == "a").TargetPosition);
    }

    [Fact]
    public void StableCandidate_NeverPreservesPositionFromAnotherDisplay()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        var icons = new[]
        {
            new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, Cells[4], "DISPLAY2", false, true),
        };

        var plan = Plan(template, icons, stable: true);

        Assert.Equal(0, plan.MoveRequiredCount);
        Assert.Single(plan.OutOfScopeIcons);
    }

    [Fact]
    public void StableCandidate_PinObstacleCannotBeDisplacedOrUsedAsTarget()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        var rules = new DesktopLayoutIconRuleSet(template.TemplateId, [
            DesktopLayoutIconRule.ForPin("pin"),
        ]);
        var icons = new[]
        {
            Icon(0, "new", new PixelPoint(17, 480)),
            Icon(1, "pin", Cells[0]),
            Icon(2, "b", Cells[1]),
            Icon(3, "c", Cells[2]),
        };

        var plan = Plan(template, icons, stable: true, rules: rules);

        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(2, plan.AlreadyAtTargetCount);
        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.Equal(Cells[3], plan.Items.Single(item => item.Icon.StableId == "new").TargetPosition);
        Assert.Equal(DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
            plan.Items.Single(item => item.Icon.StableId == "pin").PlacementState);
    }

    [Fact]
    public void StableCandidate_ZoneCapacityStillBlocksExtraDefaultIcon()
    {
        var id = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "Limited", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            [new DesktopLayoutZone(id, "Z0", new NormalizedRect(0, 0, 10000, 10000),
                0, ZonePlacementFlow.ColumnMajor, 2)]);
        var icons = new[]
        {
            Icon(0, "a", Cells[0]),
            Icon(1, "b", Cells[1]),
            Icon(2, "c", Cells[2]),
        };

        var plan = Plan(template, icons, stable: true);

        Assert.Equal(2, plan.AlreadyAtTargetCount);
        Assert.Equal(1, plan.DefaultUnassignedCount);
        Assert.False(plan.IsComplete);
    }

    [Fact]
    public void StableCandidate_TwoZonesRespectCapacityAndPreserveAlreadyAssignedSlots()
    {
        var firstZoneId = Guid.NewGuid();
        var secondZoneId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "Two zones", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            [
                new DesktopLayoutZone(firstZoneId, "Left", new NormalizedRect(0, 0, 5000, 10000),
                    0, ZonePlacementFlow.ColumnMajor, 1),
                new DesktopLayoutZone(secondZoneId, "Right", new NormalizedRect(5000, 0, 10000, 10000),
                    1, ZonePlacementFlow.ColumnMajor, 1),
            ]);
        var icons = new[]
        {
            Icon(0, "left", new PixelPoint(0, 0)),
            Icon(1, "right", new PixelPoint(960, 0)),
        };

        var plan = Plan(template, icons, stable: true);

        Assert.True(plan.IsComplete);
        Assert.Equal(2, plan.AlreadyAtTargetCount);
        Assert.Equal(0, plan.MoveRequiredCount);
        Assert.Equal(firstZoneId, plan.Items.Single(item => item.Icon.StableId == "left").ZoneId);
        Assert.Equal(secondZoneId, plan.Items.Single(item => item.Icon.StableId == "right").ZoneId);
    }

    [Fact]
    public void StableCandidate_TwoZonesDoNotOverrideExplicitDefaultZonePriority()
    {
        var firstZoneId = Guid.NewGuid();
        var secondZoneId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "Two zones", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            [
                new DesktopLayoutZone(firstZoneId, "Left", new NormalizedRect(0, 0, 5000, 10000),
                    0, ZonePlacementFlow.ColumnMajor, 1),
                new DesktopLayoutZone(secondZoneId, "Right", new NormalizedRect(5000, 0, 10000, 10000),
                    1, ZonePlacementFlow.ColumnMajor, 1),
            ]);
        var icons = new[]
        {
            Icon(0, "right", new PixelPoint(960, 0)),
            Icon(1, "left", new PixelPoint(0, 0)),
        };

        var plan = Plan(template, icons, stable: true);

        // 当前候选先按原 Ordinal/Zone.SortOrder 分桶：默认项目的跨 Zone 稳定性
        // 尚未得到产品裁决，不能因"少移动"而暗中改变旧模板含义。
        Assert.Equal(firstZoneId, plan.Items.Single(item => item.Icon.StableId == "right").ZoneId);
        Assert.Equal(secondZoneId, plan.Items.Single(item => item.Icon.StableId == "left").ZoneId);
        Assert.Equal(2, plan.MoveRequiredCount);
    }

    [Fact]
    public void StableCandidate_NegativeVirtualDesktopCoordinatesCanRemainInPlace()
    {
        var negative = new DisplayDescriptor("DISPLAY1",
            new PixelRect(-1920, -160, 1920, 1080),
            new PixelRect(-1920, -160, 1920, 1080), 96, 96, true);
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        var icons = new[]
        {
            Icon(0, "new", new PixelPoint(-1880, 240)),
            Icon(1, "a", new PixelPoint(-1920, -160)),
            Icon(2, "b", new PixelPoint(-1840, -80)),
        };

        var plan = Plan(template, icons, stable: true, displays: [negative]);

        Assert.True(plan.IsComplete);
        Assert.Equal(2, plan.AlreadyAtTargetCount);
        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.Equal(new PixelPoint(-1920, -160),
            plan.Items.Single(item => item.Icon.StableId == "a").TargetPosition);
        Assert.Equal(new PixelPoint(-1840, -80),
            plan.Items.Single(item => item.Icon.StableId == "b").TargetPosition);
    }

    [Fact]
    public void StableCandidate_ExplicitlyAuthorizedCrossDisplayOverrideIsAssignedButNotKeptRemotely()
    {
        var zoneId = Guid.NewGuid();
        var template = Template(Guid.NewGuid(), zoneId);
        var rules = new DesktopLayoutIconRuleSet(template.TemplateId, [
            DesktopLayoutIconRule.ForZoneOverride("remote", zoneId),
        ]);
        var remote = new DesktopIconSnapshotItem(0, "remote", "N_remote", null,
            ItemKind.File, new PixelPoint(1920, 0), "DISPLAY2", false, true);
        var displays = new DisplayDescriptor[]
        {
            Display,
            new("DISPLAY2", new PixelRect(1920, 0, 1920, 1080),
                new PixelRect(1920, 0, 1920, 1080), 96, 96, false),
        };

        var plan = Plan(template, [remote], stable: true, rules: rules, displays: displays,
            explicitInboundStableIds: new HashSet<string>(StringComparer.Ordinal) { "remote" });

        Assert.True(plan.IsComplete);
        Assert.Equal(1, plan.InboundMigrationIconCount);
        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.Equal(new PixelPoint(0, 0),
            plan.Items.Single(item => item.Icon.StableId == "remote").TargetPosition);
    }

    [Fact]
    public void StableCandidate_ExplicitOverrideAtHighSlotStillHonorsMaxItemCount()
    {
        var zoneId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "Cap", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            [new DesktopLayoutZone(zoneId, "Cap zone",
                new NormalizedRect(0, 0, 10000, 10000),
                0, ZonePlacementFlow.ColumnMajor, 1)]);
        var rules = new DesktopLayoutIconRuleSet(template.TemplateId, [
            DesktopLayoutIconRule.ForZoneOverride("override", zoneId),
        ]);
        var icons = new[]
        {
            Icon(0, "default", Cells[0]),
            Icon(1, "override", Cells[5]),
        };

        var plan = Plan(template, icons, stable: true, rules: rules);

        Assert.Equal(1, plan.OverrideAssignedCount);
        Assert.Equal(1, plan.DefaultUnassignedCount);
        Assert.Equal(0, plan.MoveRequiredCount);
        Assert.Equal(Cells[5], plan.Items.Single(item => item.Icon.StableId == "override").TargetPosition);
    }

    [Fact]
    public void StableCandidate_RepeatedIdenticalInputProducesIdenticalPlacements()
    {
        var template = Template(Guid.NewGuid(), Guid.NewGuid());
        var icons = new[]
        {
            Icon(0, "new", new PixelPoint(17, 480)),
            Icon(1, "a", Cells[0]),
            Icon(2, "b", Cells[1]),
            Icon(3, "c", Cells[2]),
        };

        var first = Plan(template, icons, stable: true);
        var second = Plan(template, icons, stable: true);

        Assert.Equal(
            first.Items.Select(item => (item.Icon.StableId, item.ZoneId, item.SlotIndex, item.TargetPosition)),
            second.Items.Select(item => (item.Icon.StableId, item.ZoneId, item.SlotIndex, item.TargetPosition)));
    }

}
