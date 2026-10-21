namespace DesktopAgent.Tests.Productization;

public sealed class DesktopZoneEditingProductBoundaryContractTests
{
    [Fact]
    public void DesktopEditor_IsExplicitAndKeepsExecutionInfrastructureOut()
    {
        var root = FindRepositoryRoot();
        var editor = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "DesktopZoneEditorWindow.cs"));
        var integration = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.DesktopEditor.cs"));

        Assert.Contains("不会移动桌面图标", editor, StringComparison.Ordinal);
        Assert.Contains("CapturePreviewAsync(draft)", integration, StringComparison.Ordinal);
        Assert.Contains("LayoutTemplateAndRulesSaveCoordinator.SaveAsync", integration, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsInterop.DesktopIcons", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Templates.Execution", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEditor_RenamePreviewsLiveAndSaveCopyMatchesExitBehavior()
    {
        var root = FindRepositoryRoot();
        var editor = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "DesktopZoneEditorWindow.cs"));

        Assert.Contains("_zoneNameBox.TextChanged += ZoneNameBox_TextChanged", editor, StringComparison.Ordinal);
        Assert.Contains("名称修改尚未保存", editor, StringComparison.Ordinal);
        Assert.Contains("Content = \"保存并退出\"", editor, StringComparison.Ordinal);
        Assert.Contains("Completed?.Invoke", editor, StringComparison.Ordinal);
        Assert.Contains("Cancelled?.Invoke", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryOverlay_RemainsClickThroughAndSeparateFromInteractiveEditor()
    {
        var root = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WindowsInterop",
            "DesktopOverlays",
            "DesktopZoneOverlayHost.cs"));
        var editor = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "DesktopZoneEditorWindow.cs"));

        Assert.Contains("WsExTransparent", overlay, StringComparison.Ordinal);
        Assert.Contains("HtTransparent", overlay, StringComparison.Ordinal);
        Assert.Contains("WmMouseActivate", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("WsExTransparent", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutEditingMode_IsRequiredBeforeDesktopEditorCanOpen()
    {
        var root = FindRepositoryRoot();
        var selection = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.Selection.cs"));
        var integration = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.DesktopEditor.cs"));
        var lifecycle = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.Lifecycle.cs"));

        Assert.Contains("if (_isLayoutEditing)", selection, StringComparison.Ordinal);
        Assert.Contains("await OpenDesktopZoneEditorAsync()", selection, StringComparison.Ordinal);
        Assert.Contains("if (!_isLayoutEditing)", integration, StringComparison.Ordinal);
        Assert.Contains("_desktopZoneEditorWindow is null", lifecycle, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
        }

        throw new Xunit.Sdk.XunitException("无法从测试输出目录定位 DesktopAgent.sln。\n");
    }
}
