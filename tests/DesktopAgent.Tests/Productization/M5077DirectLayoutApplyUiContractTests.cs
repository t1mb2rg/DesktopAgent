using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Tests.Productization;

public sealed class M5077DirectLayoutApplyUiContractTests
{
    [Fact]
    public void DraftExecutionSemantics_ZoneGeometryChange_InvalidatesFrozenPreview()
    {
        var original = DesktopLayoutPresetFactory.CreateNewDraft(
            "Layout",
            DesktopLayoutPresetKind.TwoColumns);
        var zones = original.Zones.ToArray();
        var first = zones[0];
        zones[0] = new DesktopLayoutZone(
            first.ZoneId,
            first.Name,
            new NormalizedRect(0, 0, 4500, 10000),
            first.SortOrder,
            first.PlacementFlow,
            first.MaxItemCount);
        zones[1] = new DesktopLayoutZone(
            zones[1].ZoneId,
            zones[1].Name,
            new NormalizedRect(4500, 0, 10000, 10000),
            zones[1].SortOrder,
            zones[1].PlacementFlow,
            zones[1].MaxItemCount);
        var changed = original.WithZones(zones);

        Assert.False(
            DesktopLayoutDirectApplyPreparationService.HasSameExecutionSemantics(
                changed,
                original));
    }

    [Fact]
    public void DraftExecutionSemantics_NameOnlyChange_DoesNotInvalidatePhysicalPreview()
    {
        var original = DesktopLayoutPresetFactory.CreateNewDraft(
            "Layout",
            DesktopLayoutPresetKind.TwoColumns);
        var renamed = original.WithMetadata(
            "Renamed only",
            original.TargetDisplayBinding);

        Assert.True(
            DesktopLayoutDirectApplyPreparationService.HasSameExecutionSemantics(
                renamed,
                original));
    }

    [Fact]
    public void ProductLayoutSurface_ExposesExplicitApplyButtonAndConfirmationDialog()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.xaml"));
        var directApply = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.DirectApply.cs"));
        var previewGate = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.PreviewGate.cs"));

        Assert.Contains("x:Name=\"ApplyToDesktopButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"应用到桌面\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ApplyToDesktop_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewLayoutWithDirectApplyGate_Click", xaml, StringComparison.Ordinal);
        // 「完整且有变化才开放应用」这条规则没变，但它现在只有一处判定
        // （Application 的 DesktopApplyReadiness），界面只负责按新证据重算。
        Assert.Contains("RefreshApplyButtonFromCurrentEvidence()", previewGate, StringComparison.Ordinal);
        var readiness = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Application",
            "DesktopLayouts",
            "Workspaces",
            "DesktopApplyReadiness.cs"));
        Assert.Contains("FreshPreviewIsComplete", readiness, StringComparison.Ordinal);
        Assert.Contains("FreshPreviewMoveRequiredCount <= 0", readiness, StringComparison.Ordinal);
        Assert.Contains("new ContentDialog", directApply, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", directApply, StringComparison.Ordinal);
        Assert.Contains("CreateFromPreview(_latestPreview)", directApply, StringComparison.Ordinal);
        Assert.Contains("HasSameExecutionSemantics", directApply, StringComparison.Ordinal);
        Assert.Contains("DirectApplyService.ApplyAsync(preparation)", directApply, StringComparison.Ordinal);
    }

    [Fact]
    public void AllProductLayoutPartials_KeepPhysicalWriterAndInternalScopeGatesOutOfWinUi()
    {
        var root = FindRepositoryRoot();
        var winUi = Path.Combine(root, "src", "DesktopAgent.WinUI");
        var files = Directory.GetFiles(winUi, "LayoutManagementView*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TargetDisplayLayoutExecutionScopeGate", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TargetDisplayLayoutStagingExecutionScopeGate", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecuteWithScopeGateAsync", source, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
        }

        throw new Xunit.Sdk.XunitException("无法从测试输出目录定位 DesktopAgent.sln。");
    }
}
