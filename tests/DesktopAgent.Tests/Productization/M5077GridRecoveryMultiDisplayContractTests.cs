namespace DesktopAgent.Tests.Productization;

public sealed class M5077GridRecoveryMultiDisplayContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void RecoverySurface_UsesProductFacade_WithoutDirectWriterAuthority()
    {
        var source = Read("src/DesktopAgent.WinUI/LayoutManagementView.Recovery.cs");

        Assert.Contains("DesktopLayoutRecoveryProductService", source, StringComparison.Ordinal);
        Assert.Contains("处理恢复状态", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistedRecoveryCoordinator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDesktopIcon", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryFacade_StopsOnMultiplePendingSessions_InsteadOfGuessingOrder()
    {
        var source = Read("src/DesktopAgent.WindowsInterop/DesktopLayouts/Restore/Execution/DesktopLayoutRecoveryProductService.cs");

        Assert.Contains("pending.Count != 1", source, StringComparison.Ordinal);
        Assert.Contains("MultiplePendingRecoveries", source, StringComparison.Ordinal);
        Assert.Contains("RecoverAsync(record", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GridPhase_IsDerivedFromFreshTargetDisplayFacts_AndFallsBackWhenUncertain()
    {
        var resolver = Read("src/DesktopAgent.Core/DesktopLayouts/Templates/Resolution/DesktopExplorerGridPhaseResolver.cs");
        var planner = Read("src/DesktopAgent.Core/DesktopLayouts/Templates/Rules/DesktopLayoutRuleAwarePlacementPlanner.cs");

        Assert.Contains("minimumConsensusRatio = 0.60d", resolver, StringComparison.Ordinal);
        Assert.Contains("return null", resolver, StringComparison.Ordinal);
        Assert.Contains("resolutionPlan.TargetDisplayIcons", planner, StringComparison.Ordinal);
        Assert.Contains("gridInference is not null", planner, StringComparison.Ordinal);
        Assert.Contains("gridInference.Phase", planner, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiDisplayCompiler_UsesLocalObservationAndKeepsSingleDisplayExecutionAtoms()
    {
        var source = Read("src/DesktopAgent.Application/DesktopLayouts/Templates/Editing/DesktopUnderstandingMultiDisplayWorkspaceCompiler.cs");

        Assert.Contains("DesktopObservation observation", source, StringComparison.Ordinal);
        Assert.Contains("MonitorDeviceName", source, StringComparison.Ordinal);
        Assert.Contains("TargetDisplayBinding.ForExactDeviceName", source, StringComparison.Ordinal);
        Assert.Contains("DesktopUnderstandingLayoutDraftCompiler.Compile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDesktopIcon", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AiProviderPayload_RemainsSemanticOnly_AfterMultiDisplayProductization()
    {
        var source = Read("src/DesktopAgent.WinUI/AiDesktopUnderstandingService.cs");
        var factsStart = source.IndexOf("private static DesktopItemFacts ToFacts", StringComparison.Ordinal);
        Assert.True(factsStart >= 0);
        var factsSection = source[factsStart..];

        Assert.DoesNotContain("MonitorDeviceName", factsSection, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelPosition", factsSection, StringComparison.Ordinal);
        Assert.Contains("Observation: observation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiDisplayWorkspace_HasExplicitSelector_AndSwitchLoadsASeparateDraft()
    {
        var xaml = Read("src/DesktopAgent.WinUI/LayoutManagementView.xaml");
        var source = Read("src/DesktopAgent.WinUI/LayoutManagementView.MultiDisplayWorkspace.cs");

        Assert.Contains("x:Name=\"AiWorkspaceDisplayCombo\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AiWorkspaceDisplayCombo_SelectionChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("PersistCurrentAiWorkspaceDraftFromEditor", source, StringComparison.Ordinal);
        Assert.Contains("LoadAiWorkspaceDraft(targetIndex)", source, StringComparison.Ordinal);
        Assert.Contains("LoadDraftIntoEditor(_draft)", source, StringComparison.Ordinal);
        Assert.Contains("LayoutTargetBindingCombo.IsEnabled = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceLifecycle_ClearsCompositeState_WhenLeavingForNormalLayout()
    {
        var source = Read("src/DesktopAgent.WinUI/LayoutManagementView.Selection.cs");

        Assert.Contains("ClearAiMultiDisplayWorkspace();", source, StringComparison.Ordinal);
        Assert.Contains("isAiMultiDisplayWorkspace", source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("找不到 DesktopAgent.sln，无法定位源码合同测试根目录。");
    }
}
