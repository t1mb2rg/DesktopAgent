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
        Assert.Contains("ContractRepairingDesktopWorkspaceDesigner", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceDesignLoopCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceFrozenPreviewFactory.Create", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceDesignPreparationFactory.CreateFromFreshObservation", service, StringComparison.Ordinal);
        Assert.Contains("var freshObservation = capture.Observation;", service, StringComparison.Ordinal);
        Assert.Contains("TryReadGridFactsAsync(freshObservation, cancellationToken)", service, StringComparison.Ordinal);
        Assert.Contains("                observation,\n                freshObservation,\n                geometryResult.Geometry,",
            service.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        var captureIndex = service.IndexOf("ProductGridCalibration.Protect(observationSource).CaptureAsync", StringComparison.Ordinal);
        var geometryIndex = service.IndexOf("DesktopExplorerWorkspaceGeometryAdapter.Build", StringComparison.Ordinal);
        var preparationIndex = service.IndexOf("DesktopWorkspaceDesignPreparationFactory.CreateFromFreshObservation", StringComparison.Ordinal);
        var designIndex = service.IndexOf("coordinator.DesignAndCompileAsync", StringComparison.Ordinal);
        Assert.True(captureIndex >= 0 && captureIndex < geometryIndex);
        Assert.True(geometryIndex < preparationIndex && preparationIndex < designIndex);

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
    public void SuccessfulWorkspacePreview_PersistsWholeImmutableRevisionHistoryAfterPreviewFormation()
    {
        var root = FindRepositoryRoot();
        var service = Read(root, "src/DesktopAgent.WinUI/AiDesktopWorkspacePlanningService.cs");
        var loop = Read(root, "src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopWorkspaceDesignLoopCoordinator.cs");

        var previewIndex = service.IndexOf("DesktopWorkspaceFrozenPreviewFactory.Create", StringComparison.Ordinal);
        var persistCallIndex = service.IndexOf("await PersistSuccessfulBlueprintAsync(", StringComparison.Ordinal);
        Assert.True(previewIndex >= 0);
        Assert.True(persistCallIndex > previewIndex);

        Assert.Contains("loop.BlueprintRevisions", service, StringComparison.Ordinal);
        Assert.Contains("sourceUserIntent", service, StringComparison.Ordinal);
        Assert.DoesNotContain("preparation.Request.UserGuidance,\n                    cancellationToken", service, StringComparison.Ordinal);
        Assert.Contains("SqliteDesktopWorkspaceBlueprintStore", service, StringComparison.Ordinal);
        Assert.Contains("store.CreateAsync", service, StringComparison.Ordinal);
        Assert.Contains("store.AppendRevisionAsync", service, StringComparison.Ordinal);
        Assert.Contains("PersistenceWarning", service, StringComparison.Ordinal);

        Assert.Contains("BlueprintRevisions", loop, StringComparison.Ordinal);
        Assert.Contains("revisionHistory.Add(blueprint)", loop, StringComparison.Ordinal);
        Assert.Contains("ValidateSuccessfulRevisionHistory", loop, StringComparison.Ordinal);
        Assert.Contains("revision.Revision != expectedRevision", loop, StringComparison.Ordinal);
        Assert.Contains("revision.ParentRevision != expectedParent", loop, StringComparison.Ordinal);

        var persistenceStart = service.IndexOf(
            "private static async Task PersistSuccessfulBlueprintAsync",
            StringComparison.Ordinal);
        var persistenceEnd = service.IndexOf(
            "private static string BuildAssetName",
            persistenceStart,
            StringComparison.Ordinal);
        Assert.True(persistenceStart >= 0);
        Assert.True(persistenceEnd > persistenceStart);
        var persistenceSection = service[persistenceStart..persistenceEnd];

        foreach (var forbidden in new[]
                 {
                     "ExactPlan",
                     "FrozenPreview",
                     "Confirmation",
                     "Writer",
                 })
        {
            Assert.DoesNotContain(forbidden, persistenceSection, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkspaceSourceIntent_TracksOnlyUserAuthoredGuidance()
    {
        var root = FindRepositoryRoot();
        var aiView = Read(root, "src/DesktopAgent.WinUI/AiAssistantView.xaml.cs");
        var import = Read(root, "src/DesktopAgent.WinUI/AiAssistantView.MultiDisplayImport.cs");
        var service = Read(root, "src/DesktopAgent.WinUI/AiDesktopWorkspacePlanningService.cs");

        Assert.Contains("DesktopWorkspaceMessageRole.User", aiView, StringComparison.Ordinal);
        Assert.Contains("_workspaceSession.Session.Snapshot().Messages", aiView, StringComparison.Ordinal);
        Assert.Contains("RememberWorkspaceUserIntent(guidance)", aiView, StringComparison.Ordinal);
        Assert.Contains("BuildWorkspaceSourceUserIntent()", aiView, StringComparison.Ordinal);
        Assert.Contains("BuildWorkspaceSourceUserIntent(),", import, StringComparison.Ordinal);
        Assert.Contains("string? sourceUserIntent", service, StringComparison.Ordinal);
        Assert.Contains("sourceUserIntent: durableUserIntent", service, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PersistSuccessfulBlueprintAsync(\n                    loop.BlueprintRevisions,\n                    preparation.Request.UserGuidance",
            service,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutSurface_HydratesReadOnlyWorkspaceLibraryWithoutRestoringExecutionAuthority()
    {
        var root = FindRepositoryRoot();
        var xaml = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.xaml");
        var library = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceLibrary.cs");

        Assert.Contains("工作区设计库", xaml, StringComparison.Ordinal);
        Assert.Contains("WorkspaceBlueprintList", xaml, StringComparison.Ordinal);
        Assert.Contains("WorkspaceLibrarySection_Loaded", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshWorkspaceLibrary_Click", xaml, StringComparison.Ordinal);

        Assert.Contains("SqliteDesktopWorkspaceBlueprintStore", library, StringComparison.Ordinal);
        Assert.Contains("ListAsync", library, StringComparison.Ordinal);
        Assert.Contains("GetCurrentRevisionAsync", library, StringComparison.Ordinal);
        Assert.Contains("设计版本 v", library, StringComparison.Ordinal);
        Assert.Contains("只读设计资产", library, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "DesktopWorkspaceFrozenPreview",
                     "CompiledWorkspacePlan",
                     "ApplyToDesktopButton",
                     "DesktopShellIconPositionWriter",
                     "IDesktopIconPositionWriter",
                     "WriterCommand",
                 })
        {
            Assert.DoesNotContain(forbidden, library, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkspaceLibraryDetail_UsesDedicatedSurfaceAndSelectableImmutableHistory_WithoutExecutionAuthority()
    {
        var root = FindRepositoryRoot();
        var xaml = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.xaml");
        var library = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceLibrary.cs");
        var detail = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceDetail.cs");

        Assert.Contains("IsItemClickEnabled = true", library, StringComparison.Ordinal);
        Assert.Contains("WorkspaceBlueprintList_ItemClick", library, StringComparison.Ordinal);

        Assert.Contains("WorkspaceDetailSurface", xaml, StringComparison.Ordinal);
        Assert.Contains("WorkspaceRevisionList", xaml, StringComparison.Ordinal);
        Assert.Contains("WorkspaceDetailBack_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("WorkspaceRevisionList_SelectionChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("分类 → 分区", xaml, StringComparison.Ordinal);
        Assert.Contains("项目例外", xaml, StringComparison.Ordinal);
        Assert.Contains("IsExpanded=\"False\"", xaml, StringComparison.Ordinal);

        Assert.Contains("GetAssetAsync", detail, StringComparison.Ordinal);
        Assert.Contains("GetRevisionAsync", detail, StringComparison.Ordinal);
        Assert.Contains("revision <= asset.CurrentRevision", detail, StringComparison.Ordinal);
        Assert.Contains("版本链不连续", detail, StringComparison.Ordinal);
        Assert.Contains("WorkspaceDetailSurface.Visibility = Visibility.Visible", detail, StringComparison.Ordinal);
        Assert.Contains("WorkspaceRevisionList.SelectedIndex = 0", detail, StringComparison.Ordinal);
        Assert.Contains("历史版本 · 只读", detail, StringComparison.Ordinal);
        Assert.Contains("LooksLikeLegacyPreparationGuidance", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialog", detail, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "DesktopWorkspaceFrozenPreview",
                     "CompiledWorkspacePlan",
                     "ApplyToDesktopButton",
                     "DesktopShellIconPositionWriter",
                     "IDesktopIconPositionWriter",
                     "WriterCommand",
                     "TargetDisplayLayoutExecutionCoordinator",
                 })
        {
            Assert.DoesNotContain(forbidden, detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkspaceDetail_LocalizesProductLanguageAndHidesRequestLocalRefs()
    {
        var root = FindRepositoryRoot();
        var xaml = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.xaml");
        var detail = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceDetail.cs");
        var library = Read(root, "src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceLibrary.cs");

        Assert.Contains("工作区设计库", xaml, StringComparison.Ordinal);
        Assert.Contains("分区", xaml, StringComparison.Ordinal);
        Assert.Contains("保留区域", xaml, StringComparison.Ordinal);
        Assert.Contains("来源用户要求", xaml, StringComparison.Ordinal);
        Assert.Contains("选择一个设计版本查看当时的方案", xaml, StringComparison.Ordinal);

        Assert.Contains("FormatWorkspaceTitle", detail, StringComparison.Ordinal);
        Assert.Contains("FormatBlueprintSummary", detail, StringComparison.Ordinal);
        Assert.Contains("FormatDisplayRef", detail, StringComparison.Ordinal);
        Assert.Contains("LocalizeUserFacingLabel", detail, StringComparison.Ordinal);
        Assert.Contains("FormatCategoryKey", detail, StringComparison.Ordinal);
        Assert.Contains("例外项目 {index + 1}", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateDetailEntry(item.ItemRef", detail, StringComparison.Ordinal);

        Assert.Contains("FormatWorkspaceTitle(summary.Name, blueprint)", library, StringComparison.Ordinal);
        Assert.Contains("FormatBlueprintSummary(blueprint)", library, StringComparison.Ordinal);
        Assert.Contains("个分区", library, StringComparison.Ordinal);
        Assert.Contains("个保留区域", library, StringComparison.Ordinal);
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
