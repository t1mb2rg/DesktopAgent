using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.Displays;

public sealed class UniqueDisplaySelectorTests
{
    private static DisplayDescriptor Display(string name) => new(
        name, new PixelRect(0, 0, 800, 600),
        new PixelRect(0, 0, 800, 600), 96, 96, true);

    [Fact]
    public void FindByDeviceName_ExactlyOneMatch_ReturnsDisplay()
    {
        var displays = new List<DisplayDescriptor>
        {
            Display(@"\\.\DISPLAY1"), Display(@"\\.\DISPLAY2"),
        };
        var result = UniqueDisplaySelector.FindByDeviceName(displays, @"\\.\DISPLAY2");
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName);
    }

    [Fact]
    public void FindByDeviceName_NoMatch_Throws()
    {
        var displays = new List<DisplayDescriptor> { Display(@"\\.\DISPLAY1") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UniqueDisplaySelector.FindByDeviceName(displays, @"\\.\DISPLAY3"));
        Assert.Contains(@"\\.\DISPLAY3", ex.Message);
        Assert.Contains("0 个匹配", ex.Message);
    }

    [Fact]
    public void FindByDeviceName_DuplicateMatch_Throws()
    {
        var displays = new List<DisplayDescriptor>
        {
            Display(@"\\.\DISPLAY1"), Display(@"\\.\DISPLAY1"),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UniqueDisplaySelector.FindByDeviceName(displays, @"\\.\DISPLAY1"));
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void FindByDeviceName_UsesOrdinalComparison()
    {
        var displays = new List<DisplayDescriptor>
        {
            Display(@"\\.\DISPLAY1"),
        };
        // 大小写不同，Ordinal 比较应视为不匹配
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UniqueDisplaySelector.FindByDeviceName(displays, @"\\.\display1"));
        Assert.Contains("0 个匹配", ex.Message);
    }

    [Fact]
    public void FindByDeviceName_NullDisplays_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            UniqueDisplaySelector.FindByDeviceName(null!, @"\\.\DISPLAY1"));
    }

    [Fact]
    public void FindByDeviceName_NullDeviceName_Throws()
    {
        var displays = new List<DisplayDescriptor> { Display(@"\\.\DISPLAY1") };
        Assert.Throws<ArgumentNullException>(() =>
            UniqueDisplaySelector.FindByDeviceName(displays, null!));
    }

    [Fact]
    public void FindByDeviceName_WhitespaceDeviceName_Throws()
    {
        var displays = new List<DisplayDescriptor> { Display(@"\\.\DISPLAY1") };
        Assert.Throws<ArgumentException>(() =>
            UniqueDisplaySelector.FindByDeviceName(displays, "   "));
    }
}
