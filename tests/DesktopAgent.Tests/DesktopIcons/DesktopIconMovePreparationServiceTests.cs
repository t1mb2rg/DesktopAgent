using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopIcons;

public sealed class DesktopIconMovePreparationServiceTests
{
    private static DesktopIconDescriptor CreateFileIcon(
        string stableId, PixelPoint pos, string? monitor = @"\\.\DISPLAY1") =>
        new(stableId, $"File_{stableId}", null, ItemKind.File, pos, monitor, false, true);

    private static DesktopIconDescriptor CreateSystemIcon(
        string stableId, PixelPoint pos, string? monitor = @"\\.\DISPLAY1") =>
        new(stableId, $"Sys_{stableId}", null, ItemKind.SystemItem, pos, monitor, true, false);

    private static DesktopIconDescriptor CreateFolderIcon(
        string stableId, PixelPoint pos, string? monitor = @"\\.\DISPLAY1") =>
        new(stableId, $"Folder_{stableId}", null, ItemKind.Folder, pos, monitor, false, true);

    private static DisplayDescriptor Display1 => new(
        @"\\.\DISPLAY1", new PixelRect(0, 0, 800, 600),
        new PixelRect(0, 0, 800, 600), 96, 96, true);

    private static DesktopIconPositioningCapabilities Capabilities(bool autoArrange = false) =>
        new(autoArrange, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null);

    [Fact]
    public void Prepare_ValidFileIcon_Succeeds()
    {
        var icon = CreateFileIcon("sid", new PixelPoint(160, 240));
        var result = new DesktopIconReadResult(
            [icon], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var prep = DesktopIconMovePreparationService.Prepare(
            "sid", result, Display1, Capabilities());

        Assert.Same(icon, prep.SelectedIcon);
        Assert.Equal("Right", prep.Plan.Direction);
    }

    [Fact]
    public void Prepare_IncompleteRead_Throws()
    {
        var result = new DesktopIconReadResult(
            [], 3, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities()));
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public void Prepare_AutoArrangeEnabled_Throws()
    {
        var icon = CreateFileIcon("sid", new PixelPoint(160, 240));
        var result = new DesktopIconReadResult(
            [icon], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities(autoArrange: true)));
        Assert.Contains("自动排列", ex.Message);
    }

    [Fact]
    public void Prepare_StableIdNotFound_Throws()
    {
        var result = new DesktopIconReadResult(
            [CreateFileIcon("other", new PixelPoint(160, 240))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities()));
        Assert.Contains("sid", ex.Message);
    }

    [Fact]
    public void Prepare_StableIdDuplicate_Throws()
    {
        var result = new DesktopIconReadResult(
            [CreateFileIcon("sid", new PixelPoint(160, 240)),
             CreateFileIcon("sid", new PixelPoint(240, 240))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities()));
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void Prepare_NonFileSystem_Throws()
    {
        var icon = new DesktopIconDescriptor(
            "sid", "name", null, ItemKind.SystemItem,
            new PixelPoint(160, 240), @"\\.\DISPLAY1", true, false);
        var result = new DesktopIconReadResult(
            [icon], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities()));
        Assert.Contains("文件系统", ex.Message);
    }

    [Fact]
    public void Prepare_SystemItem_Throws()
    {
        var icon = CreateSystemIcon("sid", new PixelPoint(160, 240));
        var result = new DesktopIconReadResult(
            [icon], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities()));
        Assert.Contains("系统", ex.Message);
    }

    [Fact]
    public void Prepare_NoMonitorDeviceName_Throws()
    {
        var icon = CreateFileIcon("sid", new PixelPoint(160, 240), monitor: null);
        var result = new DesktopIconReadResult(
            [icon], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities()));
        Assert.Contains("显示器归属", ex.Message);
    }

    [Fact]
    public void Prepare_WrongMonitor_Throws()
    {
        var icon = CreateFileIcon("sid", new PixelPoint(160, 240), @"\\.\DISPLAY2");
        var result = new DesktopIconReadResult(
            [icon], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, Capabilities()));
        Assert.Contains(@"\\.\DISPLAY2", ex.Message);
    }

    [Fact]
    public void Prepare_ExcludesSelectedIconFromOccupied()
    {
        // 两个图标，选中第一个，其余方向均被第二个挡住但 planner 应只识别第二个为占用
        var selected = CreateFileIcon("sid", new PixelPoint(160, 240));
        var other = CreateFileIcon("other", new PixelPoint(240, 240)); // Right

        var result = new DesktopIconReadResult(
            [selected, other], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        // Right 被 other 占用 → 选 Left
        var prep = DesktopIconMovePreparationService.Prepare(
            "sid", result, Display1, Capabilities());

        Assert.Equal("Left", prep.Plan.Direction);
    }

    [Fact]
    public void Prepare_ExcludesOnlyMatchedIndex()
    {
        // 选中图标通过 with 创建内容副本，验证按索引排除而非按引用
        var selected = CreateFileIcon("sid", new PixelPoint(160, 240));
        var selectedCopy = selected with { }; // 内容完全相同但不同引用
        var other = CreateFileIcon("other", new PixelPoint(240, 240)); // Right

        // readResult 中包含的是 selectedCopy（不同引用），通过 StableId 匹配到索引 0
        var result = new DesktopIconReadResult(
            [selectedCopy, other], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        // 即使 selected 与 selectedCopy 的 ReferenceEquals 为 false，
        // Prep Service 仍应按索引 0 排除，Right 被 other 占用 → 选 Left
        var prep = DesktopIconMovePreparationService.Prepare(
            "sid", result, Display1, Capabilities());

        Assert.Equal("Left", prep.Plan.Direction);
    }

    [Fact]
    public void Prepare_NoAdjacentSlot_PropagatesException()
    {
        // 图标在 (40,40)，小工作区 (0,0)-(160,160)，所有 4 个方向被占用
        var icon = CreateFileIcon("sid", new PixelPoint(40, 40));
        var allIcons = new List<DesktopIconDescriptor>
        {
            icon,                                               // (40,40)
            CreateFileIcon("a", new PixelPoint(120, 40)),          // Right
            CreateFileIcon("b", new PixelPoint(-40, 40)),         // Left — 超出 workArea
            CreateFileIcon("c", new PixelPoint(40, 120)),         // Down
            CreateFileIcon("d", new PixelPoint(40, -40)),         // Up — 超出 workArea
        };
        var smallDisplay = new DisplayDescriptor(
            @"\\.\DISPLAY1", new PixelRect(0, 0, 160, 160),
            new PixelRect(0, 0, 160, 160), 96, 96, true);
        var result = new DesktopIconReadResult(
            allIcons, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, smallDisplay, Capabilities()));
    }

    [Fact]
    public void Prepare_NullStableId_ThrowsArgumentNullException()
    {
        var result = new DesktopIconReadResult(
            [], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        Assert.Throws<ArgumentNullException>(() =>
            DesktopIconMovePreparationService.Prepare(
                null!, result, Display1, Capabilities()));
    }

    [Fact]
    public void Prepare_WhitespaceStableId_ThrowsArgumentException()
    {
        var result = new DesktopIconReadResult(
            [], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        Assert.Throws<ArgumentException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "   ", result, Display1, Capabilities()));
    }

    [Fact]
    public void Prepare_NullReadResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", null!, Display1, Capabilities()));
    }

    [Fact]
    public void Prepare_NullTargetDisplay_Throws()
    {
        var result = new DesktopIconReadResult(
            [], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        Assert.Throws<ArgumentNullException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, null!, Capabilities()));
    }

    [Fact]
    public void Prepare_NullCapabilities_Throws()
    {
        var result = new DesktopIconReadResult(
            [], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

        Assert.Throws<ArgumentNullException>(() =>
            DesktopIconMovePreparationService.Prepare(
                "sid", result, Display1, null!));
    }
}
