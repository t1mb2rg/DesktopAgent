using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopUnderstandingLayoutDraftCompilerTests
{
    [Fact]
    public void Compile_CreatesValidatedNonOverlappingDraftAndZoneOverrideRules()
    {
        var understanding = CreateUnderstanding(
            zones:
            [
                new DesktopUnderstandingZoneDraft(
                    "work", "工作", null,
                    DesktopZonePlacementHint.Left,
                    DesktopZoneSizeHint.Large,
                    ["work-category"]),
                new DesktopUnderstandingZoneDraft(
                    "leisure", "娱乐", null,
                    DesktopZonePlacementHint.Right,
                    DesktopZoneSizeHint.Compact,
                    ["leisure-category"]),
            ],
            assignments:
            [
                new DesktopUnderstandingAssignment("stable-work", "work-category", "work", 0.98d, "工作项目"),
                new DesktopUnderstandingAssignment("stable-game", "leisure-category", "leisure", 0.93d, "娱乐项目"),
            ]);

        var compiled = DesktopUnderstandingLayoutDraftCompiler.Compile(understanding);

        Assert.True(compiled.Draft.IsNew);
        Assert.Equal(2, compiled.Draft.Zones.Count);
        Assert.Equal(2, compiled.SourceAssignmentCount);
        Assert.Equal(2, compiled.MappedAssignmentCount);
        Assert.Equal(0, compiled.UnmappedAssignmentCount);
        Assert.Equal(2, compiled.Draft.RuleSet.RuleCount);
        Assert.All(compiled.Draft.RuleSet.Rules, rule =>
            Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, rule.Kind));

        var left = Assert.Single(compiled.Draft.Zones, zone => zone.Name == "工作");
        var right = Assert.Single(compiled.Draft.Zones, zone => zone.Name == "娱乐");
        Assert.True(left.Bounds.Left < right.Bounds.Left);
        Assert.True(left.Bounds.Width > right.Bounds.Width);
        Assert.False(left.Bounds.Overlaps(right.Bounds));

        _ = compiled.Draft.BuildPreviewTemplate();
    }

    [Fact]
    public void Compile_PreservesExistingZoneIdentity()
    {
        var existingZoneId = Guid.NewGuid();
        var understanding = CreateUnderstanding(
            zones:
            [
                new DesktopUnderstandingZoneDraft(
                    "existing", "已有区域", existingZoneId,
                    DesktopZonePlacementHint.Center,
                    DesktopZoneSizeHint.Medium,
                    ["work-category"]),
            ],
            assignments:
            [
                new DesktopUnderstandingAssignment("stable-work", "work-category", "existing", 0.9d, "复用现有区域"),
            ]);

        var compiled = DesktopUnderstandingLayoutDraftCompiler.Compile(understanding);

        Assert.Equal(existingZoneId, Assert.Single(compiled.Draft.Zones).ZoneId);
        Assert.Equal(existingZoneId, Assert.Single(compiled.Draft.RuleSet.Rules).TargetZoneId);
    }

    [Fact]
    public void Compile_LeavesAssignmentWithoutZoneUnmappedInsteadOfGuessing()
    {
        var understanding = CreateUnderstanding(
            zones:
            [
                new DesktopUnderstandingZoneDraft(
                    "work", "工作", null,
                    DesktopZonePlacementHint.Left,
                    DesktopZoneSizeHint.Medium,
                    ["work-category"]),
            ],
            assignments:
            [
                new DesktopUnderstandingAssignment("stable-work", "work-category", "work", 0.9d, "明确区域"),
                new DesktopUnderstandingAssignment("stable-unclear", "work-category", null, 0.4d, "区域不确定"),
            ]);

        var compiled = DesktopUnderstandingLayoutDraftCompiler.Compile(understanding);

        Assert.Equal(2, compiled.SourceAssignmentCount);
        Assert.Equal(1, compiled.MappedAssignmentCount);
        Assert.Equal(1, compiled.UnmappedAssignmentCount);
        Assert.Single(compiled.Draft.RuleSet.Rules);
        Assert.DoesNotContain(
            compiled.Draft.RuleSet.Rules,
            rule => string.Equals(rule.StableId, "stable-unclear", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_RejectsUnderstandingWithoutZones()
    {
        var understanding = CreateUnderstanding(
            zones: [],
            assignments: []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DesktopUnderstandingLayoutDraftCompiler.Compile(understanding));

        Assert.Contains("没有建议可导入", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_WithPhysicalGrid_DistributesCapacityForEveryExplicitZoneAssignment()
    {
        var zones = new[]
        {
            Zone("apps", "应用工具区", DesktopZonePlacementHint.Right, DesktopZoneSizeHint.Large),
            Zone("learning", "学习工作区", DesktopZonePlacementHint.Left, DesktopZoneSizeHint.Large),
            Zone("private", "私密资料区", DesktopZonePlacementHint.Top, DesktopZoneSizeHint.Compact),
            Zone("games", "娱乐游戏区", DesktopZonePlacementHint.Bottom, DesktopZoneSizeHint.Large),
            Zone("pending", "待整理区", DesktopZonePlacementHint.Center, DesktopZoneSizeHint.Compact),
        };
        var assignments = new List<DesktopUnderstandingAssignment>();
        AddAssignments(assignments, "apps", 52);
        AddAssignments(assignments, "learning", 23);
        AddAssignments(assignments, "private", 6);
        AddAssignments(assignments, "games", 35);
        AddAssignments(assignments, "pending", 9);
        var understanding = CreateUnderstanding(zones, assignments);
        var workArea = new PixelRect(0, 0, 1920, 1040);
        var spacing = new PixelPoint(76, 101);
        var phase = new PixelPoint(36, 28);

        var compiled = DesktopUnderstandingLayoutDraftCompiler.Compile(
            understanding,
            "容量感知布局",
            TargetDisplayBinding.ForExactDeviceName(@"\\.\DISPLAY2"),
            Array.Empty<DesktopReservedRegionConstraint>(),
            new DesktopUnderstandingLayoutDraftCompiler.PhysicalLayoutContext(
                workArea,
                spacing,
                phase));
        var resolved = DesktopLayoutTemplateResolver.Resolve(
            compiled.Draft.BuildPreviewTemplate(),
            workArea,
            spacing,
            phase);
        var demandByZoneId = compiled.Draft.RuleSet.Rules
            .GroupBy(static rule => rule.TargetZoneId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Count());

        Assert.Equal(125, compiled.MappedAssignmentCount);
        Assert.All(resolved.Zones, zone => Assert.True(
            zone.EffectiveCapacity >= demandByZoneId[zone.Zone.ZoneId],
            $"{zone.Zone.Name}: capacity={zone.EffectiveCapacity}, demand={demandByZoneId[zone.Zone.ZoneId]}"));
    }

    [Fact]
    public void Compile_WhenPhysicalGridCannotFit_DoesNotHideDemandOrInventCapacity()
    {
        var assignments = new List<DesktopUnderstandingAssignment>();
        AddAssignments(assignments, "apps", 10);
        var understanding = CreateUnderstanding(
            [Zone("apps", "应用工具区", DesktopZonePlacementHint.Center, DesktopZoneSizeHint.Large)],
            assignments);
        var workArea = new PixelRect(0, 0, 200, 200);
        var spacing = new PixelPoint(100, 100);
        var compiled = DesktopUnderstandingLayoutDraftCompiler.Compile(
            understanding,
            "容量不足布局",
            TargetDisplayBinding.ForPrimaryDisplay(),
            Array.Empty<DesktopReservedRegionConstraint>(),
            new DesktopUnderstandingLayoutDraftCompiler.PhysicalLayoutContext(
                workArea,
                spacing,
                new PixelPoint(0, 0)));
        var resolved = DesktopLayoutTemplateResolver.Resolve(
            compiled.Draft.BuildPreviewTemplate(),
            workArea,
            spacing,
            new PixelPoint(0, 0));

        Assert.Equal(10, compiled.Draft.RuleSet.RuleCount);
        Assert.True(Assert.Single(resolved.Zones).EffectiveCapacity < compiled.Draft.RuleSet.RuleCount);
    }

    private static DesktopUnderstandingZoneDraft Zone(
        string key,
        string name,
        DesktopZonePlacementHint placement,
        DesktopZoneSizeHint size)
        => new(key, name, null, placement, size, ["work-category"]);

    private static void AddAssignments(
        ICollection<DesktopUnderstandingAssignment> assignments,
        string zoneKey,
        int count)
    {
        for (var index = 0; index < count; index++)
        {
            assignments.Add(new DesktopUnderstandingAssignment(
                $"{zoneKey}-{index:D2}",
                "work-category",
                zoneKey,
                0.95d,
                "容量回归"));
        }
    }

    private static DesktopUnderstandingDraft CreateUnderstanding(
        IReadOnlyList<DesktopUnderstandingZoneDraft> zones,
        IReadOnlyList<DesktopUnderstandingAssignment> assignments)
        => new(
            "按用途整理桌面",
            "桌面包含工作与娱乐项目",
            [
                new DesktopUnderstandingCategoryDraft("work-category", "工作", "工作与开发"),
                new DesktopUnderstandingCategoryDraft("leisure-category", "娱乐", "游戏与媒体"),
            ],
            zones,
            assignments,
            questions: []);
}
