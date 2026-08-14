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
        Assert.Contains("DesktopFirstRunUnderstandingRequest", service, StringComparison.Ordinal);
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

        Assert.Contains("var facts = icons.Select(ToFacts).ToArray();", service, StringComparison.Ordinal);
        Assert.Contains("new DesktopFirstRunUnderstandingRequest(\n            facts,", service, StringComparison.Ordinal);
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
        Assert.Contains("只生成语义草案", xaml, StringComparison.Ordinal);
        Assert.Contains("不调用 Writer", xaml, StringComparison.Ordinal);
        Assert.Contains("不会移动任何桌面图标", xaml, StringComparison.Ordinal);
        Assert.Contains("正在完整理解当前桌面", codeBehind, StringComparison.Ordinal);
        Assert.Contains("随时取消", codeBehind, StringComparison.Ordinal);
        Assert.Contains("完整桌面理解超过 5 分钟", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Fresh 观察并分析了", codeBehind, StringComparison.Ordinal);
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
