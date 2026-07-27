using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Tests.DesktopLayouts.Templates;

public sealed class DesktopLayoutTemplateSummaryTests
{
    // ═══ Valid ═══

    [Fact]
    public void ValidSummary_Constructs()
    {
        var s = new DesktopLayoutTemplateSummary(
            Guid.NewGuid(), "Test", 1,
            TargetDisplayBinding.ForSelectedTarget(), 3);
        Assert.Equal("Test", s.Name);
        Assert.Equal(1, s.Version);
        Assert.Equal(3, s.ZoneCount);
        Assert.Equal(TargetDisplayBindingKind.SelectedTarget, s.TargetDisplayBinding.Kind);
    }

    // ═══ TemplateId ═══

    [Fact]
    public void EmptyTemplateId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.Empty, "T", 1,
                TargetDisplayBinding.ForSelectedTarget(), 1));
        Assert.Contains("TemplateId", ex.Message);
    }

    // ═══ Name ═══

    [Fact]
    public void NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.NewGuid(), null!, 1,
                TargetDisplayBinding.ForSelectedTarget(), 1));
    }

    [Fact]
    public void WhitespaceName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.NewGuid(), "   ", 1,
                TargetDisplayBinding.ForSelectedTarget(), 1));
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void Name_PreservesSpaces()
    {
        var s = new DesktopLayoutTemplateSummary(
            Guid.NewGuid(), "  T  ", 1,
            TargetDisplayBinding.ForSelectedTarget(), 1);
        Assert.Equal("  T  ", s.Name);
    }

    // ═══ Version ═══

    [Fact]
    public void VersionZero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.NewGuid(), "T", 0,
                TargetDisplayBinding.ForSelectedTarget(), 1));
        Assert.Contains("Version", ex.Message);
    }

    [Fact]
    public void VersionNegative_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.NewGuid(), "T", -1,
                TargetDisplayBinding.ForSelectedTarget(), 1));
        Assert.Contains("Version", ex.Message);
    }

    // ═══ Binding ═══

    [Fact]
    public void NullBinding_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.NewGuid(), "T", 1, null!, 1));
    }

    // ═══ ZoneCount ═══

    [Fact]
    public void ZoneCountZero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.NewGuid(), "T", 1,
                TargetDisplayBinding.ForSelectedTarget(), 0));
        Assert.Contains("ZoneCount", ex.Message);
    }

    [Fact]
    public void ZoneCountNegative_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutTemplateSummary(
                Guid.NewGuid(), "T", 1,
                TargetDisplayBinding.ForSelectedTarget(), -1));
        Assert.Contains("ZoneCount", ex.Message);
    }
}
