using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopIcons;

public sealed class DesktopIconPositioningModelsTests
{
    [Fact]
    public void Capabilities_ValidConstruction()
    {
        var c = new DesktopIconPositioningCapabilities(
            false, new PixelPoint(80, 80),
            ConnectionMethod.FindWindowSW, null);

        Assert.False(c.IsAutoArrangeEnabled);
        Assert.Equal(80, c.Spacing.X);
        Assert.Equal(80, c.Spacing.Y);
        Assert.Equal(ConnectionMethod.FindWindowSW, c.ConnectionMethod);
        Assert.Null(c.PrimaryPathException);
    }

    [Fact]
    public void Capabilities_SpacingXZero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopIconPositioningCapabilities(
                false, new PixelPoint(0, 80),
                ConnectionMethod.FindWindowSW, null));
        Assert.Contains("Spacing.X", ex.Message);
    }

    [Fact]
    public void Capabilities_SpacingYZero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopIconPositioningCapabilities(
                false, new PixelPoint(80, 0),
                ConnectionMethod.FindWindowSW, null));
        Assert.Contains("Spacing.Y", ex.Message);
    }

    [Fact]
    public void MoveResult_NullStableId_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopIconMoveResult(
                null!, default, default, default,
                ConnectionMethod.FindWindowSW, null));
    }

    [Fact]
    public void MoveResult_WhitespaceStableId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopIconMoveResult(
                "   ", default, default, default,
                ConnectionMethod.FindWindowSW, null));
        Assert.Contains("StableId", ex.Message);
    }

    [Fact]
    public void MoveResult_SameRequestedAndActual_IsExactTrue()
    {
        var pos = new PixelPoint(100, 200);
        var result = new DesktopIconMoveResult(
            "test_id", pos, pos, pos,
            ConnectionMethod.FindWindowSW, null);

        Assert.True(result.IsExact);
    }

    [Fact]
    public void MoveResult_DifferentRequestedAndActual_IsExactFalse()
    {
        var result = new DesktopIconMoveResult(
            "test_id",
            new PixelPoint(0, 0),
            new PixelPoint(100, 200),
            new PixelPoint(101, 201),
            ConnectionMethod.FindWindowSW, null);

        Assert.False(result.IsExact);
    }
}
