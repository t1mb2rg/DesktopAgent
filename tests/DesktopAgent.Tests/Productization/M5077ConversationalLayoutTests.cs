using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Productization;

public sealed class M5077ConversationalLayoutTests
{
    [Fact]
    public void ConversationState_AppliesDeltaWithoutRebuildingSemanticUnderstanding()
    {
        var initial = DesktopLayoutConversationState.Empty;
        var delta = new DesktopLayoutConversationDelta(
            "副屏承担大多数项目，主屏中央留空看壁纸",
            categoryDisplayTargets: new Dictionary<string, string>
            {
                ["work"] = "secondary_1",
            },
            displayLoadPreferences: new Dictionary<string, DesktopDisplayLoadPreference>
            {
                ["primary"] = DesktopDisplayLoadPreference.Low,
                ["secondary_1"] = DesktopDisplayLoadPreference.High,
            },
            upsertReservedRegions: new[]
            {
                new DesktopReservedRegionConstraint(
                    "wallpaper-center",
                    "primary",
                    DesktopZonePlacementHint.Center,
                    DesktopZoneSizeHint.Large,
                    "看清壁纸主体"),
            });

        var updated = initial.Apply(delta);

        Assert.Equal("secondary_1", updated.CategoryDisplayTargets["work"]);
        Assert.Equal(DesktopDisplayLoadPreference.Low, updated.DisplayLoadPreferences["primary"]);
        Assert.Equal(DesktopDisplayLoadPreference.High, updated.DisplayLoadPreferences["secondary_1"]);
        var reserved = Assert.Single(updated.ReservedRegions);
        Assert.Equal("wallpaper-center", reserved.Key);
        Assert.Equal(DesktopZonePlacementHint.Center, reserved.PlacementHint);
    }

    [Fact]
    public void MultiDisplayCompiler_CategoryTargetToSecondary_CreatesExplicitInboundMigration()
    {
        var understanding = CreateUnderstanding();
        var observation = CreateObservation();
        var state = DesktopLayoutConversationState.Empty.Apply(
            new DesktopLayoutConversationDelta(
                "工作项目集中到副屏",
                categoryDisplayTargets: new Dictionary<string, string>
                {
                    ["work"] = "secondary_1",
                }));

        var result = DesktopUnderstandingMultiDisplayWorkspaceCompiler.Compile(
            understanding,
            observation,
            state);

        Assert.Equal(2, result.SourceAssignmentCount);
        Assert.Equal(2, result.ScopedAssignmentCount);
        Assert.Equal(0, result.UnscopedAssignmentCount);
        Assert.Equal(1, result.CrossDisplayAssignmentCount);

        var secondary = Assert.Single(result.Displays);
        Assert.Equal(@"\\.\DISPLAY2", secondary.Display.DeviceName);
        Assert.Equal("secondary_1", secondary.DisplayRef);
        Assert.Equal(2, secondary.SourceAssignmentCount);
        Assert.Equal(1, secondary.ExplicitInboundCount);
        Assert.Contains("p-work", secondary.Draft.ExplicitInboundStableIds);

        var inboundRule = Assert.Single(
            secondary.Draft.RuleSet.Rules,
            rule => rule.StableId == "p-work");
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, inboundRule.Kind);
    }

    [Fact]
    public void ReservedCenterLarge_LeavesRealNegativeSpaceInsteadOfFullCoverage()
    {
        var understanding = CreateFourZoneUnderstanding();
        var reserved = new DesktopReservedRegionConstraint(
            "wallpaper-center",
            "primary",
            DesktopZonePlacementHint.Center,
            DesktopZoneSizeHint.Large,
            "看清壁纸主体");

        var result = DesktopUnderstandingLayoutDraftCompiler.Compile(
            understanding,
            "壁纸留白",
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { reserved });

        var center = new NormalizedRect(3000, 3000, 7000, 7000);
        Assert.Equal(4, result.Draft.Zones.Count);
        Assert.All(result.Draft.Zones, zone =>
            Assert.False(Overlaps(zone.Bounds, center),
                $"Zone {zone.Name} 不应覆盖中央壁纸保留区。"));

        var coveredArea = result.Draft.Zones.Sum(static zone =>
            (long)zone.Bounds.Width * zone.Bounds.Height);
        Assert.True(coveredArea < (long)NormalizedRect.Scale * NormalizedRect.Scale);
    }

    [Fact]
    public void EmptyReservedRegions_PreservesLegacyGeometry()
    {
        var understanding = CreateFourZoneUnderstanding();

        var legacy = DesktopUnderstandingLayoutDraftCompiler.Compile(understanding);
        var explicitEmpty = DesktopUnderstandingLayoutDraftCompiler.Compile(
            understanding,
            "AI 建议布局",
            TargetDisplayBinding.ForPrimaryDisplay(),
            Array.Empty<DesktopReservedRegionConstraint>());

        Assert.Equal(
            legacy.Draft.Zones.Select(static zone => zone.Bounds),
            explicitEmpty.Draft.Zones.Select(static zone => zone.Bounds));
    }

    [Fact]
    public void AiSurface_UsesLightweightDeltaAfterBaseModel_AndKeepsFullRefreshExplicit()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "DesktopAgent.WinUI", "AiAssistantView.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "DesktopAgent.WinUI", "AiAssistantView.xaml"));

        Assert.Contains("_layoutConversationService.PlanAsync", source, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutConversationContextBuilder.Build", source, StringComparison.Ordinal);
        Assert.Contains("_conversationState.Apply", source, StringComparison.Ordinal);
        Assert.Contains("RunFullUnderstandingAsync", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AiRefreshUnderstandingButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"重新理解桌面\"", xaml, StringComparison.Ordinal);
        Assert.Contains("应用这条修正", source, StringComparison.Ordinal);
    }

    private static DesktopUnderstandingDraft CreateUnderstanding()
    {
        var categories = new[]
        {
            new DesktopUnderstandingCategoryDraft("work", "工作", "工作项目"),
        };
        var zones = new[]
        {
            new DesktopUnderstandingZoneDraft(
                "work-zone", "工作区", null,
                DesktopZonePlacementHint.Left,
                DesktopZoneSizeHint.Large,
                new[] { "work" }),
        };
        var assignments = new[]
        {
            new DesktopUnderstandingAssignment("p-work", "work", "work-zone", 0.95d, "工作"),
            new DesktopUnderstandingAssignment("s-work", "work", "work-zone", 0.95d, "工作"),
        };
        return new DesktopUnderstandingDraft(
            "整理工作项目",
            "双屏工作桌面",
            categories,
            zones,
            assignments,
            Array.Empty<DesktopUnderstandingQuestion>());
    }

    private static DesktopUnderstandingDraft CreateFourZoneUnderstanding()
    {
        var categories = Enumerable.Range(0, 4)
            .Select(index => new DesktopUnderstandingCategoryDraft(
                $"c{index}", $"分类 {index}", $"分类 {index}"))
            .ToArray();
        var hints = new[]
        {
            DesktopZonePlacementHint.TopLeft,
            DesktopZonePlacementHint.TopRight,
            DesktopZonePlacementHint.BottomLeft,
            DesktopZonePlacementHint.BottomRight,
        };
        var zones = Enumerable.Range(0, 4)
            .Select(index => new DesktopUnderstandingZoneDraft(
                $"z{index}",
                $"区域 {index}",
                null,
                hints[index],
                DesktopZoneSizeHint.Medium,
                new[] { $"c{index}" }))
            .ToArray();
        var assignments = Enumerable.Range(0, 4)
            .Select(index => new DesktopUnderstandingAssignment(
                $"i{index}",
                $"c{index}",
                $"z{index}",
                0.9d,
                "测试"))
            .ToArray();
        return new DesktopUnderstandingDraft(
            "四角布局",
            "四类项目",
            categories,
            zones,
            assignments,
            Array.Empty<DesktopUnderstandingQuestion>());
    }

    private static DesktopObservation CreateObservation()
    {
        var display1 = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var display2 = new DisplayDescriptor(
            @"\\.\DISPLAY2",
            new PixelRect(-1080, 0, 0, 1920),
            new PixelRect(-1080, 0, 0, 1920),
            96,
            96,
            false);

        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { display1, display2 },
            new DesktopIconPositioningCapabilities(
                false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                null),
            new[]
            {
                Icon("p-work", 80, 80, display1.DeviceName),
                Icon("s-work", -1000, 80, display2.DeviceName),
            },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));
    }

    private static DesktopIconDescriptor Icon(string stableId, int x, int y, string monitor)
        => new(
            stableId,
            stableId,
            null,
            ItemKind.File,
            new PixelPoint(x, y),
            monitor,
            false,
            true);

    private static bool Overlaps(NormalizedRect a, NormalizedRect b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static string FindRepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
        }

        throw new InvalidOperationException("找不到 DesktopAgent.sln。");
    }
}
