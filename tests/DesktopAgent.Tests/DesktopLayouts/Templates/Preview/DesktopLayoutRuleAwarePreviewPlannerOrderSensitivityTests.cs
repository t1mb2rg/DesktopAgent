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
        DesktopLayoutIconRuleSet? rules = null)
        => DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            rules ?? DesktopLayoutIconRuleSet.Empty(template.TemplateId),
            new[] { Display },
            null,
            Spacing,
            icons,
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
            Icon(0, "new", Cells[6]),
            Icon(1, "a", Cells[0]),
            Icon(2, "b", Cells[1]),
            Icon(3, "c", Cells[2]),
            Icon(4, "d", Cells[3]),
            Icon(5, "e", Cells[4]),
        };

        var plan = Plan(template, icons, stable: true);

        Assert.Equal(5, plan.AlreadyAtTargetCount);
        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.Equal("new", Assert.Single(plan.Items.Where(item => item.RequiresMove)).Icon.StableId);
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
            Icon(0, "a", Cells[4]) with { MonitorDeviceName = "DISPLAY2" },
        };

        var plan = Plan(template, icons, stable: true);

        Assert.Equal(0, plan.MoveRequiredCount);
        Assert.Single(plan.OutOfScopeItems);
    }

}
