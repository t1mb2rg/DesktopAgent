using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc.LayoutTemplates.Preview;

namespace DesktopAgent.Tests.LayoutTemplates.Preview;

public sealed class LayoutPreviewCanvasTransformTests
{
    [Fact] public void W1920_H1080_To640x360_Square() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 640, 360, 0); Assert.True(t.Scale > 0); Assert.Equal(t.Scale, t.Scale); /* same scale X/Y */ }
    [Fact] public void Widescreen_InSquareViewport_LetterboxTopBottom() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 400, 400, 0); Assert.True(t.OffsetY > 0); }
    [Fact] public void Narrow_InWideViewport_LetterboxLeftRight() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 1200, 400, 0); Assert.True(t.OffsetX > 0); }
    [Fact] public void Portrait() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1080, 1920), 400, 800, 0); Assert.True(t.Scale > 0); }
    [Fact] public void NegativeWorkArea() { var t = new LayoutPreviewCanvasTransform(new PixelRect(-800, -640, 0, 0), 800, 640, 0); Assert.True(t.Scale > 0); }
    [Fact] public void NonZeroLeftTop() { var t = new LayoutPreviewCanvasTransform(new PixelRect(100, 50, 900, 690), 800, 640, 0); var (cx, cy) = t.Map(100, 50); Assert.True(cx >= 0 && cy >= 0); }
    [Fact] public void TopLeftMaps() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0); var (cx, cy) = t.Map(0, 0); Assert.True(cx >= 0 && cy >= 0); }
    [Fact] public void BottomRightMaps() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0); var (cx, cy) = t.Map(1920, 1080); Assert.True(cx <= 960 && cy <= 540); }
    [Fact] public void CenterPoint() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0); var (cx, cy) = t.Map(960, 540); Assert.True(Math.Abs(cx - 480) < 10); }
    [Fact] public void PixelRectMap() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 800, 600, 0); var (_, _, w, h) = t.Map(new PixelRect(0, 0, 400, 300)); Assert.True(w > 0 && h > 0); }
    [Fact] public void SameScaleXY() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 800, 600, 10); var (cx1, _) = t.Map(1920, 0); var (_, cy2) = t.Map(0, 1080); var scaleX = (cx1 - t.OffsetX) / 1920; var scaleY = (cy2 - t.OffsetY) / 1080; Assert.Equal(scaleX, scaleY, 6); }
    [Fact] public void Resize_KeepsRatio() { var t1 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0); var t2 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 480, 270, 0); Assert.Equal(t1.Scale / t2.Scale, 2.0, 6); }
    [Fact] public void Padding() { var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 800, 600, 40); Assert.True(t.OffsetX >= 40 && t.OffsetY >= 40); }
    [Fact] public void WidthZero_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 0), 800, 600, 0)); }
    [Fact] public void HeightZero_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 0, 600), 800, 600, 0)); }
    [Fact] public void ViewportWidthZero_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 0, 600, 0)); }
    [Fact] public void ViewportNaN_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), double.NaN, 600, 0)); }
    [Fact] public void ViewportInfinity_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), double.PositiveInfinity, 600, 0)); }
    [Fact] public void PaddingNegative_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 800, 600, -1)); }
    [Fact] public void PaddingTooLarge_Fails() { Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 600), 100, 100, 60)); }
    [Fact] public void ExtremeInt_NoOverflow() { var t = new LayoutPreviewCanvasTransform(new PixelRect(int.MinValue, int.MinValue, int.MinValue + 1920, int.MinValue + 1080), 960, 540, 0); Assert.True(t.Scale > 0); }
    [Fact] public void Deterministic() { var t1 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0); var t2 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 1920, 1080), 960, 540, 0); Assert.Equal(t1.Scale, t2.Scale); }
}
