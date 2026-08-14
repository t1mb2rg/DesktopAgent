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
    public void ProductAiService_UsesNinetySecondBudgetOnlyForWholeDesktopUnderstanding()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));

        Assert.Contains(
            "ProductUnderstandingTimeout = TimeSpan.FromSeconds(90)",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "configuredOptions.SecretReference,\n            ProductUnderstandingTimeout",
            service,
            StringComparison.Ordinal);
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
    public void AiSurface_ExposesProviderProbeAndStatesSemanticDraftSafetyBoundary()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml"));

        Assert.Contains("测试 AI 连接", xaml, StringComparison.Ordinal);
        Assert.Contains("TestAiConnection_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("只生成语义草案", xaml, StringComparison.Ordinal);
        Assert.Contains("不调用 Writer", xaml, StringComparison.Ordinal);
        Assert.Contains("不会移动任何桌面图标", xaml, StringComparison.Ordinal);
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
