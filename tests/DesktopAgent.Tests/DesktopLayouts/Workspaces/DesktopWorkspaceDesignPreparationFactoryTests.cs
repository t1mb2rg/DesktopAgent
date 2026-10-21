using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopWorkspaceDesignPreparationFactoryTests
{
    private static (DesktopUnderstandingDraft Understanding, DesktopObservation Observation,
        DesktopLayoutConversationState Conversation) CreateFixture()
    {
        const string localStableId = "LOCAL-SENSITIVE-STABLE-ID";
        var observationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var displays = CreateDisplays();
        var observation = new DesktopObservation(
            observationId,
            DateTimeOffset.UtcNow,
            displays,
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            [
                new DesktopIconDescriptor(
                    localStableId,
                    "VS Code",
                    null,
                    ItemKind.File,
                    new PixelPoint(100, 100),
                    "\\\\.\\DISPLAY1",
                    IsSystemItem: false,
                    IsFileSystemItem: true),
                new DesktopIconDescriptor(
                    "LEFT-ITEM",
                    "Left Note",
                    null,
                    ItemKind.File,
                    new PixelPoint(-1800, 100),
                    "\\\\.\\DISPLAY2",
                    IsSystemItem: false,
                    IsFileSystemItem: true),
                new DesktopIconDescriptor(
                    "SYSTEM-ITEM",
                    "Recycle Bin",
                    null,
                    ItemKind.SystemItem,
                    new PixelPoint(20, 20),
                    "\\\\.\\DISPLAY1",
                    IsSystemItem: false,
                    IsFileSystemItem: false),
            ],
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);
        var understanding = new DesktopUnderstandingDraft(
            "用户希望把可移动项目放到下方副屏，主屏中央留白，左副屏保持现状。",
            "三屏桌面，包含开发工具、左屏资料和系统项目。",
            [
                new DesktopUnderstandingCategoryDraft("dev", "开发工具", "IDE 和工程工具"),
                new DesktopUnderstandingCategoryDraft("docs", "文档", "资料"),
                new DesktopUnderstandingCategoryDraft("system", "系统项目", "Windows 系统项目"),
            ],
            zones:
            [
                new DesktopUnderstandingZoneDraft(
                    "legacy-zone",
                    "Legacy Zone",
                    existingZoneId: null,
                    DesktopZonePlacementHint.Left,
                    DesktopZoneSizeHint.Large,
                    ["dev", "docs", "system"]),
            ],
            assignments:
            [
                new DesktopUnderstandingAssignment(localStableId, "dev", "legacy-zone", 1, "dev"),
                new DesktopUnderstandingAssignment("LEFT-ITEM", "docs", "legacy-zone", 1, "docs"),
                new DesktopUnderstandingAssignment("SYSTEM-ITEM", "system", "legacy-zone", 1, "system"),
            ]);
        var conversation = new DesktopLayoutConversationState(
            "把所有可移动图标整理到下方副屏；主屏中间留出大块空白；左边副屏保持现在的状态。",
            categoryDisplayTargets: new Dictionary<string, string>(StringComparer.Ordinal),
            itemDisplayTargets: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [localStableId] = "secondary_2",
            },
            displayLoadPreferences: new Dictionary<string, DesktopDisplayLoadPreference>(StringComparer.Ordinal),
            reservedRegions:
            [
                new DesktopReservedRegionConstraint(
                    "wallpaper-center",
                    "primary",
                    DesktopZonePlacementHint.Center,
                    DesktopZoneSizeHint.Large,
                    "保持壁纸中央可见"),
            ]);

        return (understanding, observation, conversation);
    }

    [Fact]
    public void Create_ThreeDisplayTopology_LabelsLeftAndLowerConsistently_AndKeepsStableIdsLocal()
    {
        const string localStableId = "LOCAL-SENSITIVE-STABLE-ID";
        var (understanding, observation, conversation) = CreateFixture();
        var observationId = observation.ObservationId;
        var geometry = CreateGeometry(observationId);
        var preparation = DesktopWorkspaceDesignPreparationFactory.Create(
            understanding,
            observation,
            geometry,
            conversation);

        Assert.Equal(3, preparation.Request.Displays.Count);
        Assert.Equal("主屏", preparation.Request.Displays.Single(display => display.DisplayRef == "primary").Label);
        Assert.Equal("左副屏", preparation.Request.Displays.Single(display => display.DisplayRef == "secondary_1").Label);
        Assert.Equal("下方副屏", preparation.Request.Displays.Single(display => display.DisplayRef == "secondary_2").Label);

        var binding = Assert.Single(preparation.ItemRefBindings, binding =>
            string.Equals(binding.StableId, localStableId, StringComparison.Ordinal));
        Assert.Contains($"项目 {binding.ItemRef} 的目标屏为 secondary_2", preparation.Request.UserGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain(localStableId, preparation.Request.UserGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-zone", preparation.Request.UserGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-zone", preparation.SemanticUnderstanding.Assignments.Select(static item => item.CategoryKey));

        Assert.Equal(observationId, preparation.SourceSnapshot.ObservationId);
        Assert.Equal(3, preparation.ItemFacts.Count);
        Assert.Equal(3, preparation.SourceSnapshot.ItemFacts.Count);
        Assert.Contains(preparation.Request.CriticalItems, item => !item.IsMovable && item.Label == "Recycle Bin");
        Assert.Contains(preparation.Request.CriticalItems, item => item.ItemRef == binding.ItemRef && item.Label == "VS Code");
    }

    [Fact]
    public void CreateFromFreshObservation_MovedAcrossDisplays_UsesFreshSourceFactsAndPreservesSemantics()
    {
        var (understanding, source, conversation) = CreateFixture();
        var fresh = WithIcons(source, source.Icons.Select(icon => icon.StableId == "LEFT-ITEM"
            ? icon with { PixelPosition = new PixelPoint(100, 1600), MonitorDeviceName = "\\\\.\\DISPLAY3" }
            : icon));

        var preparation = DesktopWorkspaceDesignPreparationFactory.CreateFromFreshObservation(
            understanding, source, fresh, CreateGeometry(fresh.ObservationId), conversation);

        Assert.NotEqual(source.ObservationId, preparation.SourceSnapshot.ObservationId);
        Assert.Equal(fresh.ObservationId, preparation.SourceSnapshot.ObservationId);
        var fact = Assert.Single(preparation.ItemFacts, item => item.StableId == "LEFT-ITEM");
        Assert.Equal("secondary_2", fact.CurrentDisplayRef);
        Assert.Equal(new PixelPoint(100, 1600), fact.PixelPosition);
        Assert.Contains(fact, preparation.SourceSnapshot.ItemFacts);
        Assert.Contains(preparation.SemanticUnderstanding.Assignments,
            item => item.StableId == "LEFT-ITEM" && item.CategoryKey == "docs");
        Assert.Equal(new PixelPoint(-1800, 100), source.Icons.Single(icon => icon.StableId == "LEFT-ITEM").PixelPosition);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("kind")]
    [InlineData("system")]
    [InlineData("add")]
    [InlineData("remove")]
    public void CreateFromFreshObservation_SemanticFactsChanged_RequiresNewUnderstanding(string change)
    {
        var (understanding, source, conversation) = CreateFixture();
        var icons = source.Icons.ToList();
        switch (change)
        {
            case "rename": icons[0] = icons[0] with { DisplayName = "Renamed" }; break;
            case "kind": icons[0] = icons[0] with { ItemKind = ItemKind.SystemItem }; break;
            case "system": icons[0] = icons[0] with { IsSystemItem = true }; break;
            case "add": icons.Add(icons[0] with { StableId = "NEW-ITEM" }); break;
            case "remove": icons.RemoveAt(0); break;
        }
        var fresh = WithIcons(source, icons);

        var error = Assert.Throws<InvalidDataException>(() =>
            DesktopWorkspaceDesignPreparationFactory.CreateFromFreshObservation(
                understanding, source, fresh, CreateGeometry(fresh.ObservationId), conversation));

        Assert.Contains("请先重新理解桌面", error.Message);
    }

    [Fact]
    public void CreateFromFreshObservation_OldGeometryCannotBeCombinedWithFreshPositions()
    {
        var (understanding, source, conversation) = CreateFixture();
        var fresh = WithIcons(source, source.Icons);

        Assert.Throws<ArgumentException>(() =>
            DesktopWorkspaceDesignPreparationFactory.CreateFromFreshObservation(
                understanding, source, fresh, CreateGeometry(source.ObservationId), conversation));
    }

    private static DesktopObservation WithIcons(DesktopObservation source, IEnumerable<DesktopIconDescriptor> icons)
        => new(Guid.NewGuid(), DateTimeOffset.UtcNow, source.Displays, source.PositioningCapabilities,
            icons.ToArray(), source.IconReadConnectionMethod, source.IconReadTime);

    private static IReadOnlyList<DisplayDescriptor> CreateDisplays()
        => new[]
        {
            new DisplayDescriptor(
                "\\\\.\\DISPLAY1",
                new PixelRect(0, 0, 2560, 1440),
                new PixelRect(0, 0, 2560, 1440),
                96,
                96,
                IsPrimary: true),
            new DisplayDescriptor(
                "\\\\.\\DISPLAY2",
                new PixelRect(-1920, 0, 0, 1080),
                new PixelRect(-1920, 0, 0, 1080),
                96,
                96,
                IsPrimary: false),
            new DisplayDescriptor(
                "\\\\.\\DISPLAY3",
                new PixelRect(0, 1440, 1920, 2520),
                new PixelRect(0, 1440, 1920, 2520),
                96,
                96,
                IsPrimary: false),
        };

    private static ExplorerWorkspaceGeometry CreateGeometry(Guid observationId)
    {
        var spacing = new PixelPoint(80, 80);
        var phase = new PixelPoint(20, 20);
        var primary = new ExplorerDisplayGeometry(
            "primary",
            "\\\\.\\DISPLAY1",
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1440),
            true,
            96,
            96,
            spacing,
            phase,
            new PixelPoint(20, 20),
            1,
            1,
            [new ExplorerNativeSlot("primary:slot:0000:0000", 0, 0, new PixelPoint(20, 20))]);
        var left = new ExplorerDisplayGeometry(
            "secondary_1",
            "\\\\.\\DISPLAY2",
            new PixelRect(-1920, 0, 0, 1080),
            new PixelRect(-1920, 0, 0, 1080),
            false,
            96,
            96,
            spacing,
            phase,
            new PixelPoint(-1900, 20),
            1,
            1,
            [new ExplorerNativeSlot("secondary_1:slot:0000:0000", 0, 0, new PixelPoint(-1900, 20))]);
        var lower = new ExplorerDisplayGeometry(
            "secondary_2",
            "\\\\.\\DISPLAY3",
            new PixelRect(0, 1440, 1920, 2520),
            new PixelRect(0, 1440, 1920, 2520),
            false,
            96,
            96,
            spacing,
            phase,
            new PixelPoint(20, 1460),
            1,
            1,
            [new ExplorerNativeSlot("secondary_2:slot:0000:0000", 0, 0, new PixelPoint(20, 1460))]);

        return new ExplorerWorkspaceGeometry(
            observationId,
            "geometry:test-three-display",
            isAutoArrangeEnabled: false,
            spacing,
            [primary, left, lower]);
    }
}
