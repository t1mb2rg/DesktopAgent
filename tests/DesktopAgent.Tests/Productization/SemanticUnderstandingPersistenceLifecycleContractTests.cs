namespace DesktopAgent.Tests.Productization;

public sealed class SemanticUnderstandingPersistenceLifecycleContractTests
{
    [Fact]
    public void StartupRestore_UsesLocalSnapshotAndFreshObservationWithoutProviderOrWriter()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));

        var restoreStart = service.IndexOf(
            "internal async Task<AiPersistedUnderstandingProductState> RestoreLatestAsync",
            StringComparison.Ordinal);
        var analyzeStart = service.IndexOf(
            "internal async Task<AiDesktopUnderstandingProductResult> AnalyzeAsync",
            StringComparison.Ordinal);

        Assert.True(restoreStart >= 0);
        Assert.True(analyzeStart > restoreStart);

        var restore = service[restoreStart..analyzeStart];
        Assert.Contains("SqliteDesktopSemanticUnderstandingStore", restore, StringComparison.Ordinal);
        Assert.Contains("GetLatestAsync", restore, StringComparison.Ordinal);
        Assert.Contains("CaptureObservationAsync", restore, StringComparison.Ordinal);
        Assert.Contains("DesktopSemanticUnderstandingFreshnessEvaluator.Evaluate", restore, StringComparison.Ordinal);

        Assert.DoesNotContain("OpenAiCompatible", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("DpapiIntelligenceSecretStore", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("Execution", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulFullUnderstanding_PersistsPureSemanticsButKeepsPersistenceFailureNonFatal()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiDesktopUnderstandingService.cs"));
        var surface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml.cs"));

        Assert.Contains("PersistSuccessfulUnderstandingAsync", service, StringComparison.Ordinal);
        Assert.Contains("DesktopSemanticUnderstandingProjector.FromLegacyDraft", service, StringComparison.Ordinal);
        Assert.Contains("DesktopSemanticUnderstandingSourceFingerprint.Create", service, StringComparison.Ordinal);
        Assert.Contains("SqliteDesktopSemanticUnderstandingStore", service, StringComparison.Ordinal);
        Assert.Contains("PersistenceWarning", service, StringComparison.Ordinal);
        Assert.Contains("理解已建立，但未能写入长期记忆", service, StringComparison.Ordinal);

        Assert.Contains("result.PersistenceWarning", surface, StringComparison.Ordinal);
        Assert.Contains("桌面语义模型已建立，但长期记忆未保存", surface, StringComparison.Ordinal);
        Assert.Contains("本次会话可以继续使用", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedUnderstanding_HydratesReadOnlyAndCannotUnlockWorkspacePreviewAuthority()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml"));
        var persistenceSurface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.Persistence.cs"));
        var mainSurface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml.cs"));

        Assert.Contains("Loaded=\"AiAssistantView_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RestoreLatestAsync", persistenceSurface, StringComparison.Ordinal);
        Assert.Contains("AiImportLayoutButton.IsEnabled = false", persistenceSurface, StringComparison.Ordinal);
        Assert.Contains("持久化 Understanding 不包含 Zone 或显示器设计", persistenceSurface, StringComparison.Ordinal);
        Assert.Contains("执行确认不会跨重启恢复", persistenceSurface, StringComparison.Ordinal);

        Assert.Contains(
            "AiImportLayoutButton.IsEnabled = !busy && _latestSuccessfulUnderstanding?.Draft is not null",
            mainSurface,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_latestSuccessfulUnderstanding =", persistenceSurface, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", persistenceSurface, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", persistenceSurface, StringComparison.Ordinal);
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
