using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopSemanticUnderstandingTests
{
    [Fact]
    public void FromLegacyDraft_DropsPlanningAuthorityAndPreservesSemanticFacts()
    {
        var legacy = new DesktopUnderstandingDraft(
            "把开发工具整理到右边。",
            "桌面以开发工具和课程资料为主。",
            [
                new DesktopUnderstandingCategoryDraft("dev", "开发工具", "IDE、终端和工程工具"),
                new DesktopUnderstandingCategoryDraft("study", "学习资料", "课程与文档"),
            ],
            [
                new DesktopUnderstandingZoneDraft(
                    "tools-zone",
                    "工具区",
                    existingZoneId: null,
                    DesktopZonePlacementHint.Right,
                    DesktopZoneSizeHint.Medium,
                    ["dev"]),
                new DesktopUnderstandingZoneDraft(
                    "study-zone",
                    "学习区",
                    existingZoneId: null,
                    DesktopZonePlacementHint.Left,
                    DesktopZoneSizeHint.Large,
                    ["study"]),
            ],
            [
                new DesktopUnderstandingAssignment(
                    "stable-vscode",
                    "dev",
                    "tools-zone",
                    0.96,
                    "名称和类型符合开发工具。"),
                new DesktopUnderstandingAssignment(
                    "stable-report",
                    "study",
                    "study-zone",
                    0.82,
                    "文件名符合课程资料。"),
            ],
            [new DesktopUnderstandingQuestion("q1", "这个未知文件属于课程资料吗？")]);

        var semantic = DesktopSemanticUnderstandingProjector.FromLegacyDraft(legacy);

        Assert.Equal("桌面以开发工具和课程资料为主。", semantic.DesktopSummary);
        Assert.Equal(2, semantic.Categories.Count);
        Assert.Equal(2, semantic.Assignments.Count);
        Assert.Single(semantic.Questions);

        var dev = semantic.Assignments.Single(item => item.StableId == "stable-vscode");
        Assert.Equal("dev", dev.CategoryKey);
        Assert.Equal(0.96, dev.Confidence);
        Assert.Equal("名称和类型符合开发工具。", dev.Reason);

        Assert.DoesNotContain(
            typeof(DesktopSemanticUnderstanding).GetProperties(),
            property => property.Name.Contains("Zone", StringComparison.OrdinalIgnoreCase));
        Assert.Null(typeof(DesktopSemanticUnderstanding).GetProperty("IntentSummary"));
        Assert.Null(typeof(DesktopSemanticAssignment).GetProperty("ZoneKey"));
    }

    [Fact]
    public void Contract_RejectsAssignmentReferencingUnknownCategory()
    {
        Assert.Throws<ArgumentException>(() => new DesktopSemanticUnderstanding(
            "summary",
            [new DesktopSemanticCategory("known", "Known", "known category")],
            [new DesktopSemanticAssignment("item", "missing", 0.5, "uncertain")],
            []));
    }

    [Fact]
    public void Contract_RejectsDuplicateAssignmentIdentity()
    {
        Assert.Throws<ArgumentException>(() => new DesktopSemanticUnderstanding(
            "summary",
            [new DesktopSemanticCategory("dev", "Development", "developer tools")],
            [
                new DesktopSemanticAssignment("same-item", "dev", 0.8, "first"),
                new DesktopSemanticAssignment("same-item", "dev", 0.9, "second"),
            ],
            []));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Assignment_RejectsInvalidConfidence(double confidence)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopSemanticAssignment("item", "dev", confidence, "reason"));

    [Fact]
    public void LegacyDraft_RemainsAvailableForCompatibility()
    {
        var legacy = new DesktopUnderstandingDraft(
            "legacy intent",
            "legacy summary",
            [new DesktopUnderstandingCategoryDraft("dev", "Development", "developer tools")],
            [
                new DesktopUnderstandingZoneDraft(
                    "legacy-zone",
                    "Legacy Zone",
                    existingZoneId: null,
                    DesktopZonePlacementHint.Left,
                    DesktopZoneSizeHint.Compact,
                    ["dev"]),
            ],
            [new DesktopUnderstandingAssignment("item", "dev", "legacy-zone", 1d, "legacy")]);

        Assert.Single(legacy.Zones);
        Assert.Equal("legacy-zone", legacy.Assignments[0].ZoneKey);

        var semantic = DesktopSemanticUnderstandingProjector.FromLegacyDraft(legacy);
        Assert.Single(semantic.Assignments);
        Assert.Null(typeof(DesktopSemanticAssignment).GetProperty("ZoneKey"));
    }
}
