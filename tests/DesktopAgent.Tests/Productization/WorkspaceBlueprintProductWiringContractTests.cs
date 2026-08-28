namespace DesktopAgent.Tests.Productization;

public sealed class WorkspaceBlueprintProductWiringContractTests
{
    [Fact]
    public void WorkspaceProductService_UsesBlueprintCompilerCertificateAndFrozenPreviewWithoutWriterAuthority()
    {
        var root = FindRepositoryRoot();
        var service = Read(root, "src/DesktopAgent.WinUI/AiDesktopWorkspacePlanningService.cs");

        Assert.Contains("DesktopExplorerWorkspaceGeometryAdapter.Build", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceDesignPreparationFactory.Create", service, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatibleDesktopWorkspaceDesigner", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceDesignLoopCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceFrozenPreviewFactory.Create", service, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "DesktopShellIconPositionWriter",
                     "IDesktopIconPositionWriter",
                     "TargetDisplayLayoutExecutionCoordinator",
                     "PersistedRecoveryCoordinator",
                     "WriterCommand",
                 })
        {
            Assert.DoesNotContain(forbidden, service, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MultiDisplayImportButton_InvokesNewWorkspacePreviewPath_WhileLegacyPathRemainsAvailable()
    {
        var root = FindRepositoryRoot();
        var aiView = Read(root, "src/DesktopAgent.WinUI/AiAssistantView.xaml.cs");
        var workspaceFields = Read(root, "src/DesktopAgent.WinUI/AiAssistantView.WorkspacePlanning.cs");
        var import = Read(root, "src/DesktopAgent.WinUI/AiAssistantView.MultiDisplayImport.cs");
        var host = Read(root, "src/DesktopAgent.WinUI/MainWindow.AiAssistant.cs");

        Assert.Contains("AiDesktopWorkspacePlanningService", workspaceFields, StringComparison.Ordinal);
        Assert.Contains("WorkspaceFrozenPreviewRequested", workspaceFields, StringComparison.Ordinal);
        Assert.Contains("_workspacePlanningService.PlanAsync", import, StringComparison.Ordinal);
        Assert.Contains("WorkspaceFrozenPreviewRequested.Invoke", import, StringComparison.Ordinal);
        Assert.Contains("TryLoadAiWorkspaceFrozenPreview", host, StringComparison.Ordinal);
        Assert.Contains("PrepareWorkspaceFrozenPreviewReplacement", host, StringComparison.Ordinal);

        // Phase 7-C 暂不删除 legacy exact-cell 产品合同。
        Assert.Contains("AiDesktopLogicalWorkspacePlanningService", aiView, StringComparison.Ordinal);
        Assert.Contains("LogicalWorkspacePreviewRequested", aiView, StringComparison.Ordinal);
    }

    [Fact]
    public void Preparation_ReplacesConversationStableIdsWithRequestLocalItemRefs()
    {
        var root = FindRepositoryRoot();
        var preparation = Read(
            root,
            "src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopWorkspaceDesignPreparationFactory.cs");

        Assert.Contains("itemRefByStableId", preparation, StringComparison.Ordinal);
        Assert.Contains("项目 {itemRef} 的目标屏为 {target.Value}", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("项目 {target.Key} 的目标屏为 {target.Value}", preparation, StringComparison.Ordinal);
        Assert.Contains("DesktopSemanticUnderstandingProjector.FromLegacyDraft", preparation, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspacePreviewSourceSnapshot", preparation, StringComparison.Ordinal);
    }

    private static string Read(string root, string relativePath)
        => File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
