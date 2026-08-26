using System.Text.Json;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopLogicalWorkspaceSemanticOverlayTests
{
    [Fact]
    public void Apply_BindsCategoriesByLocalStableIdWithoutLeakingAuthority()
    {
        var projection = DesktopLogicalWorkspaceProjector.Project(CreateObservation());
        var understanding = CreateUnderstanding(
        [
            new DesktopUnderstandingAssignment(
                "secret-stable-system",
                "system",
                null,
                1d,
                "系统项目"),
            new DesktopUnderstandingAssignment(
                "secret-stable-a",
                "work",
                null,
                0.95d,
                "工作资料"),
        ]);

        var result = DesktopLogicalWorkspaceSemanticOverlay.Apply(projection, understanding);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Workspace!.Categories.Count);
        Assert.Equal("工作资料", result.Workspace.Categories[0].Name);
        Assert.Equal(
            "work",
            Assert.Single(result.Workspace.Items, static item => item.DisplayName == "Secondary.txt").CategoryKey);
        Assert.Equal(
            "system",
            Assert.Single(result.Workspace.Items, static item => item.DisplayName == "回收站").CategoryKey);
        Assert.Null(
            Assert.Single(result.Workspace.Items, static item => item.DisplayName == "Primary.txt").CategoryKey);
        Assert.Same(projection.Authority, result.Authority);

        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            "工作资料",
            document.RootElement
                .GetProperty("Workspace")
                .GetProperty("Categories")[0]
                .GetProperty("Name")
                .GetString());
        Assert.Contains("work", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-stable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY2", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("GridOrigin", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelPosition", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_UnknownAssignmentStableId_FailsClosedWithoutRetainingAuthorityOrIdentity()
    {
        var projection = DesktopLogicalWorkspaceProjector.Project(CreateObservation());
        var understanding = CreateUnderstanding(
        [
            new DesktopUnderstandingAssignment(
                "secret-stable-outside-observation",
                "work",
                null,
                0.8d,
                "陌生项目"),
        ]);

        var result = DesktopLogicalWorkspaceSemanticOverlay.Apply(projection, understanding);

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Null(result.Authority);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(
            DesktopLogicalWorkspaceProjectionIssueCode.SemanticAssignmentOutsideObservation,
            issue.Code);
        Assert.DoesNotContain("secret-stable", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_CategoryKeysCollideAfterNormalization_FailsClosed()
    {
        var projection = DesktopLogicalWorkspaceProjector.Project(CreateObservation());
        var understanding = new DesktopUnderstandingDraft(
            "按类别整理",
            "分类 key 存在歧义",
            [
                new DesktopUnderstandingCategoryDraft("work", "工作资料", "工作"),
                new DesktopUnderstandingCategoryDraft(" work ", "另一工作类", "歧义分类"),
            ]);

        var result = DesktopLogicalWorkspaceSemanticOverlay.Apply(projection, understanding);

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Null(result.Authority);
        Assert.Equal(
            DesktopLogicalWorkspaceProjectionIssueCode.InvalidSemanticCategoryCatalog,
            Assert.Single(result.Issues).Code);
    }

    private static DesktopUnderstandingDraft CreateUnderstanding(
        IReadOnlyList<DesktopUnderstandingAssignment> assignments) =>
        new(
            "按类别整理",
            "工作资料与系统项目",
            [
                new DesktopUnderstandingCategoryDraft("work", "工作资料", "需要集中处理的文件"),
                new DesktopUnderstandingCategoryDraft("system", "系统项目", "固定的系统入口"),
            ],
            assignments: assignments);

    private static DesktopObservation CreateObservation() =>
        new(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            DateTimeOffset.Parse("2026-08-25T13:00:00Z"),
            [
                new DisplayDescriptor(
                    @"\\.\DISPLAY1",
                    new PixelRect(-1_000, 0, 0, 600),
                    new PixelRect(-1_000, 0, 0, 600),
                    96,
                    96,
                    true),
                new DisplayDescriptor(
                    @"\\.\DISPLAY2",
                    new PixelRect(0, 0, 800, 600),
                    new PixelRect(0, 0, 800, 600),
                    144,
                    144,
                    false),
            ],
            new DesktopIconPositioningCapabilities(
                false,
                new PixelPoint(100, 100),
                ConnectionMethod.FindWindowSW,
                null),
            [
                new DesktopIconDescriptor(
                    "secret-stable-a",
                    "Secondary.txt",
                    @"C:\Secret\Secondary.txt",
                    ItemKind.File,
                    new PixelPoint(10, 20),
                    @"\\.\DISPLAY2",
                    false,
                    true),
                new DesktopIconDescriptor(
                    "secret-stable-b",
                    "Primary.txt",
                    @"C:\Secret\Primary.txt",
                    ItemKind.File,
                    new PixelPoint(-990, 20),
                    @"\\.\DISPLAY1",
                    false,
                    true),
                new DesktopIconDescriptor(
                    "secret-stable-system",
                    "回收站",
                    "::{645FF040-5081-101B-9F08-00AA002F954E}",
                    ItemKind.SystemItem,
                    new PixelPoint(-890, 120),
                    @"\\.\DISPLAY1",
                    true,
                    false),
            ],
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(10));
}
