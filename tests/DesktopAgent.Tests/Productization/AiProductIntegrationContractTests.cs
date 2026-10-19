namespace DesktopAgent.Tests.Productization;

public sealed class AiProductIntegrationContractTests
{
    [Fact]
    public void ProductAiService_ReusesFrozenM4ProviderAndNeverReferencesExecutionWriter()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));

        Assert.Contains("DesktopProductIntelligenceOptionsFactory.Create", service, StringComparison.Ordinal);
        Assert.Contains("WindowsDesktopObservationSource", service, StringComparison.Ordinal);
        Assert.Contains("DesktopObservationUnderstandingRequestFactory.Create", service, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatibleDesktopFirstRunIntelligenceProvider", service, StringComparison.Ordinal);
        Assert.Contains("DpapiIntelligenceSecretStore", service, StringComparison.Ordinal);

        Assert.DoesNotContain("DesktopShellIconPositionWriter", service, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopIconMovePlanner", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopLayoutExecution", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductAiService_AiDisabledGateHappensBeforeFreshDesktopObservation()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));

        var optionsFactory = service.IndexOf(
            "var configuredOptions = DesktopProductIntelligenceOptionsFactory.Create(settings);",
            StringComparison.Ordinal);
        var disabledGate = service.IndexOf("if (configuredOptions is null)", StringComparison.Ordinal);
        var capture = service.IndexOf("observationSource.CaptureAsync", StringComparison.Ordinal);

        Assert.True(optionsFactory >= 0);
        Assert.True(disabledGate > optionsFactory);
        Assert.True(capture > disabledGate);
    }

    [Fact]
    public void ProductAiService_UsesFiveMinuteBudgetOnlyForWholeDesktopUnderstanding()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));

        Assert.Contains(
            "ProductUnderstandingTimeout = TimeSpan.FromMinutes(5)",
            service,
            StringComparison.Ordinal);

        var optionsStart = service.IndexOf(
            "var options = new OpenAiCompatibleProviderOptions(",
            StringComparison.Ordinal);
        Assert.True(optionsStart >= 0);

        var optionsEnd = service.IndexOf(");", optionsStart, StringComparison.Ordinal);
        Assert.True(optionsEnd > optionsStart);

        var optionsConstruction = service[optionsStart..(optionsEnd + 2)];
        Assert.Contains("configuredOptions.BaseUri", optionsConstruction, StringComparison.Ordinal);
        Assert.Contains("configuredOptions.Model", optionsConstruction, StringComparison.Ordinal);
        Assert.Contains("configuredOptions.SecretReference", optionsConstruction, StringComparison.Ordinal);
        Assert.Contains("ProductUnderstandingTimeout", optionsConstruction, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductAiService_PassesAllFreshObservedItemsIntoFrozenM4UnderstandingProvider()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));

        Assert.Contains("DesktopObservationUnderstandingRequestFactory.Create", service, StringComparison.Ordinal);
        Assert.Contains("observation", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductUnderstandingSampleLimit", service, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildRepresentativeSample", service, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyzedItemCount", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductAiProbe_UsesExistingM4ClassificationProviderWithoutDesktopObservation()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));

        var probeStart = service.IndexOf("internal async Task<AiProviderProbeProductResult> ProbeAsync", StringComparison.Ordinal);
        var analyzeStart = service.IndexOf("internal async Task<AiDesktopUnderstandingProductResult> AnalyzeAsync", StringComparison.Ordinal);

        Assert.True(probeStart >= 0);
        Assert.True(analyzeStart > probeStart);

        var probeSection = service[probeStart..analyzeStart];
        Assert.Contains("OpenAiCompatibleDesktopIntelligenceProvider", probeSection, StringComparison.Ordinal);
        Assert.Contains("Tessel Provider Probe.txt", probeSection, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsDesktopObservationSource", probeSection, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", probeSection, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", probeSection, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSurface_IsLazyAndLeavingOrHidingCancelsActiveRequest()
    {
        var root = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "MainWindow.AiAssistant.cs"));
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "MainWindow.xaml.cs"));

        Assert.Contains("private AiAssistantView? _aiAssistantView;", host, StringComparison.Ordinal);
        Assert.Contains("EnsureAiAssistantView", host, StringComparison.Ordinal);
        Assert.Contains("CancelActiveRequest", host, StringComparison.Ordinal);
        Assert.Contains("ReleaseAiAssistantView", host, StringComparison.Ordinal);
        Assert.Contains("EnsureAiAssistantView();", window, StringComparison.Ordinal);
        Assert.Contains("CancelAiAssistantRequest();", window, StringComparison.Ordinal);
        Assert.Contains("ReleaseAiAssistantView();", window, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSurface_ExposesProviderProbeFullUnderstandingAndSemanticDraftSafetyBoundary()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml.cs"));

        Assert.Contains("测试 AI 连接", xaml, StringComparison.Ordinal);
        Assert.Contains("TestAiConnection_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("全部项目", xaml, StringComparison.Ordinal);
        Assert.Contains("可能需要几分钟", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("代表性样本", xaml, StringComparison.Ordinal);
        Assert.Contains("分析和设计不会移动图标", xaml, StringComparison.Ordinal);
        Assert.Contains("检查每块屏幕的布局", xaml, StringComparison.Ordinal);
        Assert.Contains("确认是否应用", xaml, StringComparison.Ordinal);
        Assert.Contains("正在建立桌面语义模型", codeBehind, StringComparison.Ordinal);
        Assert.Contains("正在重新理解当前桌面", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CancelAnalysis_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AiCancelButton.Visibility", codeBehind, StringComparison.Ordinal);
        Assert.Contains("完整桌面理解超过 5 分钟", codeBehind, StringComparison.Ordinal);
        Assert.Contains("已完整理解", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalWorkspacePlanner_ValidatesCompletePlanAndNeverReferencesExecutionTypes()
    {
        var root = FindRepositoryRoot();
        var planner = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Application",
            "Intelligence",
            "OpenAiCompatibleDesktopLogicalWorkspacePlanner.cs"));
        var contract = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Application",
            "Intelligence",
            "DesktopLogicalWorkspacePlanningContract.cs"));

        Assert.Contains("DesktopDesiredWorkspacePlanValidator.Validate", planner, StringComparison.Ordinal);
        Assert.Contains("DesktopObservedLogicalWorkspace", contract, StringComparison.Ordinal);
        Assert.Contains("ValidatedPlan", contract, StringComparison.Ordinal);
        Assert.Contains("RejectedPlan", contract, StringComparison.Ordinal);
        Assert.Contains("CanRetryValidation", contract, StringComparison.Ordinal);

        var planningSource = planner + contract;
        Assert.DoesNotContain("IDesktopIconPositionWriter", planningSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", planningSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopIconMovePlanner", planningSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", planningSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopLayoutExecution", planningSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalWorkspaceProductService_UsesFreshSafeSessionAndUiWiresOnlyFrozenPreview()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopLogicalWorkspacePlanningService.cs"));
        var aiSurface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml.cs"));
        var importSurface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.MultiDisplayImport.cs"));
        var previewSurface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.LogicalWorkspacePreview.cs"));

        Assert.Contains("DesktopProductIntelligenceOptionsFactory.Create", service, StringComparison.Ordinal);
        Assert.Contains("WindowsDesktopObservationSource", service, StringComparison.Ordinal);
        Assert.Contains("DesktopObservationUnderstandingRequestFactory.Create", service, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatibleDesktopFirstRunIntelligenceProvider", service, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatibleDesktopLogicalWorkspacePlanner", service, StringComparison.Ordinal);
        Assert.Contains("DesktopLogicalWorkspacePlanningSessionCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("understanding.Draft", service, StringComparison.Ordinal);

        var optionsGate = service.IndexOf("if (configuredOptions is null)", StringComparison.Ordinal);
        var capture = service.IndexOf("observationSource.CaptureAsync", StringComparison.Ordinal);
        Assert.True(optionsGate >= 0);
        Assert.True(capture > optionsGate);

        Assert.DoesNotContain("DesktopUnderstandingMultiDisplayWorkspaceCompiler", service, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAiCompatibleDesktopLayoutConversationProvider", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", service, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopLayoutExecution", service, StringComparison.Ordinal);
        Assert.Contains("AiDesktopLogicalWorkspacePlanningService", aiSurface, StringComparison.Ordinal);
        Assert.Contains("LogicalWorkspacePreviewRequested", aiSurface, StringComparison.Ordinal);
        Assert.Contains("BuildLogicalWorkspacePlanningGuidance", importSurface, StringComparison.Ordinal);
        Assert.Contains("FrozenPreview", importSurface, StringComparison.Ordinal);
        Assert.Contains("TryLoadAiLogicalWorkspacePreview", previewSurface, StringComparison.Ordinal);

        foreach (var source in new[] { importSurface, previewSurface })
        {
            Assert.DoesNotContain("DesktopUnderstandingMultiDisplayWorkspaceCompiler", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WriterCommand", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LogicalWorkspaceSemanticOverlay_BindsLocallyAndNeverFallsBackToNameMatching()
    {
        var root = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Application",
            "DesktopLayouts",
            "Workspaces",
            "DesktopLogicalWorkspaceSemanticOverlay.cs"));

        Assert.Contains("TryResolveItemByStableId", overlay, StringComparison.Ordinal);
        Assert.Contains("DesktopObservedLogicalCategory", overlay, StringComparison.Ordinal);
        Assert.Contains("SemanticAssignmentOutsideObservation", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayName ==", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", overlay, StringComparison.Ordinal);
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
