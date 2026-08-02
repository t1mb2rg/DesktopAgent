using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc.LayoutTemplates.Preview;

namespace DesktopAgent.Tests.LayoutTemplates.Preview;

public sealed class LayoutPreviewCanvasTransformTests
{
    private static void AssertApprox(double expected, double actual, double tolerance = 0.01)
        => Assert.True(Math.Abs(expected - actual) < tolerance, $"Expected ~{expected}, actual {actual}");

    [Fact] public void Exact1920x1080_To960x540_NoPadding()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0);
        AssertApprox(0.5, t.Scale);
        AssertApprox(0, t.OffsetX);
        AssertApprox(0, t.OffsetY);
        var (cx, cy) = t.Map(1920, 1080);
        AssertApprox(960, cx); AssertApprox(540, cy);
    }

    [Fact] public void Widescreen_SquareVP_LetterboxTopBottom()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 400, 400, 0);
        AssertApprox(400.0 / 1920, t.Scale);
        AssertApprox(0, t.OffsetX);
        Assert.True(t.OffsetY > 0);
        var expectedH = 1080 * t.Scale;
        AssertApprox(0, t.OffsetX);
        AssertApprox((400 - expectedH) / 2, t.OffsetY);
    }

    [Fact] public void Narrow_WideVP_LetterboxLeftRight()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 1200, 400, 0);
        AssertApprox(400.0 / 600, t.Scale);
        Assert.True(t.OffsetX > 0);
        AssertApprox(0, t.OffsetY);
    }

    [Fact] public void Portrait_1080x1920()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1080, 1920), 400, 800, 0);
        AssertApprox(400.0 / 1080, t.Scale);
        AssertApprox(0, t.OffsetX);
    }

    [Fact] public void NegativeWorkArea_Maps()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(-800, -640, 0, 0), 800, 640, 0);
        AssertApprox(1.0, t.Scale);
        var (cx, cy) = t.Map(-800, -640);
        AssertApprox(0, cx); AssertApprox(0, cy);
    }

    [Fact] public void NonZeroLeftTop()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(100, 50, 900, 690), 800, 640, 0);
        var (cx, cy) = t.Map(100, 50);
        AssertApprox(0, cx); AssertApprox(0, cy);
    }

    [Fact] public void TopLeftMapsToOffset()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0);
        var (cx, cy) = t.Map(0, 0);
        AssertApprox(0, cx); AssertApprox(0, cy);
    }

    [Fact] public void BottomRightMaps()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0);
        var (cx, cy) = t.Map(1920, 1080);
        AssertApprox(960, cx); AssertApprox(540, cy);
    }

    [Fact] public void CenterPoint()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0);
        var (cx, cy) = t.Map(960, 540);
        AssertApprox(480, cx); AssertApprox(270, cy);
    }

    [Fact] public void PixelRectMap()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 800, 600, 0);
        var (_, _, w, h) = t.Map(new PixelRect(0, 0, 400, 300));
        AssertApprox(400, w); AssertApprox(300, h);
    }

    [Fact] public void SameScaleXY()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 800, 600, 10);
        var (cx1, _) = t.Map(1920, 0);
        var (_, cy2) = t.Map(0, 1080);
        AssertApprox((cx1 - t.OffsetX) / 1920, (cy2 - t.OffsetY) / 1080);
    }

    [Fact] public void ResizeHalfViewport_ScaleAndOffsetChange()
    {
        var t1 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0);
        var t2 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 480, 270, 0);
        AssertApprox(t1.Scale, t2.Scale * 2.0);
    }

    [Fact] public void Padding()
    {
        var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 800, 600, 40);
        Assert.True(t.OffsetX >= 40 && t.OffsetY >= 40);
    }

    [Fact] public void WidthZero_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 0, 600), 800, 600, 0)); }
    [Fact] public void HeightZero_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 0), 800, 600, 0)); }
    [Fact] public void ViewportWidthZero_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 0, 600, 0)); }
    [Fact] public void ViewportNaN_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), double.NaN, 600, 0)); }
    [Fact] public void ViewportInfinity_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), double.PositiveInfinity, 600, 0)); }
    [Fact] public void PaddingNegative_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 800, 600, -1)); }
    [Fact] public void PaddingTooLarge_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 100, 100, 60)); }
    [Fact] public void ExtremeInt_NoOverflow()
    {
        var t = new LayoutPreviewCanvasTransform(
            new PixelRect(int.MinValue, int.MinValue, int.MinValue + 1920, int.MinValue + 1080), 960, 540, 0);
        AssertApprox(0.5, t.Scale);
        var (cx, cy) = t.Map(int.MinValue, int.MinValue);
        AssertApprox(0, cx); AssertApprox(0, cy);
    }
    [Fact] public void Deterministic()
    {
        var t1 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0);
        var t2 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0);
        AssertApprox(t1.Scale, t2.Scale);
        AssertApprox(t1.OffsetX, t2.OffsetX);
        AssertApprox(t1.OffsetY, t2.OffsetY);
    }
}
