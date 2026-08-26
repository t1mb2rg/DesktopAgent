namespace DesktopAgent.Tests.Productization;

public sealed class AiLayoutImportContractTests
{
    [Fact]
    public void AiLayoutCompilerAndImportSurface_DoNotReferencePersistenceOrWriterAuthority()
    {
        var root = FindRepositoryRoot();
        var compiler = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Application",
            "DesktopLayouts",
            "Templates",
            "Editing",
            "DesktopUnderstandingLayoutDraftCompiler.cs"));
        var importSurface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.AiImport.cs"));

        Assert.Contains("DesktopLayoutEditDraft", compiler, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutIconRule.ForZoneOverride", compiler, StringComparison.Ordinal);
        Assert.Contains("TryLoadAiImportedDraft", importSurface, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "DesktopShellIconPositionWriter",
                     "WriterCommand",
                     "SqliteDesktopLayoutTemplateStore",
                     "SaveAsync(",
                     "BuildNextPersistedTemplate",
                 })
        {
            Assert.DoesNotContain(forbidden, compiler, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, importSurface, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AiLayoutImport_RequiresExplicitImportAndProtectsExistingUnsavedEdit()
    {
        var root = FindRepositoryRoot();
        var aiView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.xaml"));
        var importSurface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.AiImport.cs"));
        var host = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "MainWindow.AiAssistant.cs"));

        Assert.Contains("生成精确布局预览", aiView, StringComparison.Ordinal);
        Assert.Contains("本地必须完整校验", aiView, StringComparison.Ordinal);
        Assert.Contains("_isLayoutEditing && _draft is not null", importSurface, StringComparison.Ordinal);
        Assert.Contains("AI 草案没有覆盖当前未保存修改", importSurface, StringComparison.Ordinal);
        Assert.Contains("LayoutDraftImportRequested", host, StringComparison.Ordinal);
        Assert.Contains("TryLoadAiImportedDraft", host, StringComparison.Ordinal);
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
