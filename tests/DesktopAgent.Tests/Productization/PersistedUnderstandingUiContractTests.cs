namespace DesktopAgent.Tests.Productization;

public sealed class PersistedUnderstandingUiContractTests
{
    [Fact]
    public void RestoredPureUnderstanding_ShowsOnlyOneFullReunderstandingEntryPoint()
    {
        var root = FindRepositoryRoot();
        var persistenceView = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.Persistence.cs"));

        Assert.Contains(
            "AiRefreshUnderstandingButton.Visibility = Visibility.Collapsed;",
            persistenceView,
            StringComparison.Ordinal);
        Assert.Contains(
            "AiAnalyzeButton.Content = \"重新理解桌面\";",
            persistenceView,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AiRefreshUnderstandingButton.Visibility = Visibility.Visible;",
            persistenceView,
            StringComparison.Ordinal);
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
