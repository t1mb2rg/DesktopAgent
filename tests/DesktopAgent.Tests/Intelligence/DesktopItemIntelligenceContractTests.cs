using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopItemIntelligenceContractTests
{
    [Fact]
    public void Facts_Valid_PreservesExactValues()
    {
        var facts = new DesktopItemFacts(
            "  stable-id  ",
            "  实验报告.PDF  ",
            ItemKind.File,
            ".PDF",
            isSystemItem: false,
            isFileSystemItem: true);

        Assert.Equal("  stable-id  ", facts.StableId);
        Assert.Equal("  实验报告.PDF  ", facts.DisplayName);
        Assert.Equal(ItemKind.File, facts.ItemKind);
        Assert.Equal(".PDF", facts.FileExtension);
        Assert.False(facts.IsSystemItem);
        Assert.True(facts.IsFileSystemItem);
    }

    [Fact]
    public void Facts_NullExtension_IsAllowed()
    {
        var facts = new DesktopItemFacts("id", "回收站", ItemKind.SystemItem, null, true, false);
        Assert.Null(facts.FileExtension);
    }

    [Fact]
    public void Facts_EmptyStableId_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemFacts("", "name", ItemKind.File, ".txt", false, true));

    [Fact]
    public void Facts_EmptyDisplayName_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemFacts("id", "", ItemKind.File, ".txt", false, true));

    [Fact]
    public void Facts_UndefinedItemKind_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemFacts("id", "name", (ItemKind)99, null, false, false));

    [Fact]
    public void Facts_EmptyExtension_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemFacts("id", "name", ItemKind.File, "", false, true));

    [Fact]
    public void Facts_ExtensionWithoutDot_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemFacts("id", "name", ItemKind.File, "pdf", false, true));

    [Fact]
    public void Suggestion_Valid_AllowsSemanticOnlyResult()
    {
        var suggestion = new DesktopItemClassificationSuggestion(
            "CourseDocument",
            null,
            0.87,
            "文件名和类型与课程资料一致。",
            DesktopItemClassificationSource.AiProvider);

        Assert.Equal("CourseDocument", suggestion.Category);
        Assert.Null(suggestion.SuggestedZoneId);
        Assert.Equal(0.87, suggestion.Confidence);
        Assert.Equal(DesktopItemClassificationSource.AiProvider, suggestion.Source);
    }

    [Fact]
    public void Suggestion_Valid_AllowsZoneRecommendation()
    {
        var zoneId = Guid.NewGuid();
        var suggestion = new DesktopItemClassificationSuggestion(
            "Development",
            zoneId,
            1d,
            "用户已有明确偏好。",
            DesktopItemClassificationSource.UserProfile);

        Assert.Equal(zoneId, suggestion.SuggestedZoneId);
        Assert.Equal(1d, suggestion.Confidence);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Suggestion_InvalidConfidence_Fails(double confidence)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopItemClassificationSuggestion(
                "Category",
                null,
                confidence,
                "reason",
                DesktopItemClassificationSource.AiProvider));

    [Fact]
    public void Suggestion_EmptyGuid_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemClassificationSuggestion(
                "Category",
                Guid.Empty,
                0.5,
                "reason",
                DesktopItemClassificationSource.LocalRule));

    [Fact]
    public void Suggestion_UndefinedSource_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemClassificationSuggestion(
                "Category",
                null,
                0.5,
                "reason",
                (DesktopItemClassificationSource)99));

    [Fact]
    public void Decision_Valid_IsSeparateFromSuggestion()
    {
        var zoneId = Guid.NewGuid();
        var decision = new DesktopItemClassificationDecision(
            "CourseDocument",
            zoneId,
            0.95,
            DesktopItemClassificationSource.UserRule,
            "用户明确规则覆盖其他建议。");

        Assert.Equal("CourseDocument", decision.Category);
        Assert.Equal(zoneId, decision.TargetZoneId);
        Assert.Equal(0.95, decision.Confidence);
        Assert.Equal(DesktopItemClassificationSource.UserRule, decision.Source);
        Assert.Equal("用户明确规则覆盖其他建议。", decision.Explanation);
    }

    [Fact]
    public void Decision_EmptyExplanation_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopItemClassificationDecision(
                "Category",
                null,
                0.5,
                DesktopItemClassificationSource.LocalRule,
                ""));

    [Fact]
    public void ClassificationSource_NumericOrder_DoesNotEncodePriority()
    {
        Assert.True(Enum.IsDefined(DesktopItemClassificationSource.UserRule));
        Assert.True(Enum.IsDefined(DesktopItemClassificationSource.LocalRule));
        Assert.True(Enum.IsDefined(DesktopItemClassificationSource.UserProfile));
        Assert.True(Enum.IsDefined(DesktopItemClassificationSource.AiProvider));
    }
}
