using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class ExplorerGridTopologyIdentityTests
{
    private static readonly PixelPoint Spacing = new(93, 122);

    [Fact]
    public void Identity_IsStable_ForTheSameTopology()
    {
        var first = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]);
        var second = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]);

        Assert.Equal(first, second);
        Assert.StartsWith("explorer-topology-v1:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_DoesNotDependOnDisplayOrder()
    {
        var a = Display("DISPLAY1", 0, 0, 1920, 1080, 96, true);
        var b = Display("DISPLAY2", 1920, 0, 3840, 1080, 96, false);

        Assert.Equal(
            ExplorerGridTopologyIdentity.Compute(false, Spacing, [a, b]),
            ExplorerGridTopologyIdentity.Compute(false, Spacing, [b, a]));
    }

    [Fact]
    public void Identity_ChangesWhenWorkAreaChanges_BecauseTaskbarIsPartOfTheEnvironment()
    {
        var before = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]);
        var after = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 48, 1920, 1080, 96, true)]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Identity_ChangesWhenScalingChanges()
    {
        var before = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]);
        var after = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 144, true)]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Identity_ChangesWhenAutoArrangeChanges()
    {
        var off = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]);
        var on = ExplorerGridTopologyIdentity.Compute(true, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]);

        Assert.NotEqual(off, on);
    }

    [Fact]
    public void Identity_ChangesWhenSpacingChanges()
    {
        Assert.NotEqual(
            ExplorerGridTopologyIdentity.Compute(false, new PixelPoint(93, 122), [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]),
            ExplorerGridTopologyIdentity.Compute(false, new PixelPoint(96, 122), [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]));
    }

    [Fact]
    public void Identity_ChangesWhenDisplaySetChanges()
    {
        var one = ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]);
        var two = ExplorerGridTopologyIdentity.Compute(false, Spacing,
            [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true), Display("DISPLAY2", 1920, 0, 3840, 1080, 96, false)]);

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void Identity_RejectsInputsThatCannotDescribeAnEnvironment()
    {
        Assert.Throws<ArgumentException>(() =>
            ExplorerGridTopologyIdentity.Compute(false, Spacing, []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExplorerGridTopologyIdentity.Compute(false, new PixelPoint(0, 122), [Display("DISPLAY1", 0, 0, 1920, 1080, 96, true)]));
        Assert.Throws<ArgumentException>(() =>
            ExplorerGridTopologyIdentity.Compute(false, Spacing, [Display("   ", 0, 0, 1920, 1080, 96, true)]));
    }

    private static DisplayDescriptor Display(
        string deviceName,
        int left,
        int top,
        int right,
        int bottom,
        uint dpi,
        bool isPrimary) =>
        new(
            deviceName,
            new PixelRect(left, top, right, bottom),
            new PixelRect(left, top, right, bottom),
            dpi,
            dpi,
            isPrimary);
}
