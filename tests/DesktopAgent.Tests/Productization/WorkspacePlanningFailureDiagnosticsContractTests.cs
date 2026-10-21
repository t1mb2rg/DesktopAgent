namespace DesktopAgent.Tests.Productization;

public sealed class WorkspacePlanningFailureDiagnosticsContractTests
{
    [Fact]
    public void WorkspacePlanningFailure_ShowsBoundedCompilerDiagnosticsWithoutStableIds()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "AiAssistantView.MultiDisplayImport.cs"));

        Assert.Contains("BuildWorkspacePlanningFailureMessage(result)", source, StringComparison.Ordinal);
        Assert.Contains("result.CompileIssues", source, StringComparison.Ordinal);
        Assert.Contains(".Take(3)", source, StringComparison.Ordinal);
        Assert.Contains("ZoneCapacityInsufficient", source, StringComparison.Ordinal);
        Assert.Contains("RequiredSlots", source, StringComparison.Ordinal);
        Assert.Contains("AvailableSlots", source, StringComparison.Ordinal);
        Assert.Contains("本地 Compiler 最后诊断", source, StringComparison.Ordinal);
        Assert.Contains("没有生成 Frozen Preview，也没有调用 Writer", source, StringComparison.Ordinal);

        Assert.DoesNotContain("issue.StableId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("issue.Message", source, StringComparison.Ordinal);
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
