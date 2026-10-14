using System.Text;

namespace DesktopAgent.Tests.Productization;

/// <summary>
/// 「应用到桌面」此前被 6 个界面 partial 里的 18 处写入各自改写：判定没有单一出处，
/// 于是"点不动但不知道缺什么"。这些断言守的是收拢后的形状——
/// 判定在 <c>DesktopApplyReadiness</c>（可穷举测试），界面只允许一个渲染/写按钮的地方。
/// </summary>
public sealed class ProductApplyEntryContractTests
{
    private static readonly string[] GatePartials =
    [
        "LayoutManagementView.DirectApply.cs",
        "LayoutManagementView.PreviewGate.cs",
        "LayoutManagementView.Recovery.cs",
        "LayoutManagementView.LogicalWorkspacePreview.cs",
        "LayoutManagementView.WorkspaceFrozenPreview.cs",
        "LayoutManagementView.WorkspaceApply.cs",
    ];

    [Fact]
    public void OnlyTheReadinessPartial_WritesTheApplyButton()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(
                     Path.Combine(root, "src", "DesktopAgent.WinUI"),
                     "LayoutManagementView*.cs"))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "LayoutManagementView.Readiness.cs", StringComparison.Ordinal))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("ApplyToDesktopButton.IsEnabled", StringComparison.Ordinal) ||
                text.Contains("ApplyToDesktopButton.Content", StringComparison.Ordinal) ||
                text.Contains("ApplyReadinessHeadlineText", StringComparison.Ordinal) ||
                text.Contains("ApplyReadinessDetailText", StringComparison.Ordinal))
            {
                offenders.Add(name);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void GatePathways_RefreshThroughTheReadinessPartialInsteadOfDecidingLocally()
    {
        var root = FindRepositoryRoot();
        var winUi = Path.Combine(root, "src", "DesktopAgent.WinUI");

        foreach (var name in GatePartials)
        {
            var text = File.ReadAllText(Path.Combine(winUi, name));
            Assert.True(
                text.Contains("RefreshApplyButtonFromCurrentEvidence()", StringComparison.Ordinal) ||
                text.Contains("BlockApplyForCurrentEvidence(", StringComparison.Ordinal),
                $"{name} 没有把「应用到桌面」的状态交给就绪判定。");
        }

        // 旧的就地判定必须消失，否则会出现第二个真相。
        var previewGate = File.ReadAllText(Path.Combine(winUi, "LayoutManagementView.PreviewGate.cs"));
        Assert.DoesNotContain("preview.Plan.MoveRequiredCount > 0", previewGate, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyToDesktopButton", previewGate, StringComparison.Ordinal);

        var workspaceApply = File.ReadAllText(Path.Combine(winUi, "LayoutManagementView.WorkspaceApply.cs"));
        Assert.DoesNotContain("ApplyToDesktopButton", workspaceApply, StringComparison.Ordinal);
        Assert.DoesNotContain("_latestPreview.Plan.IsComplete", workspaceApply, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessBar_IsDeclaredBeforeTheActionRow()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.xaml"));

        Assert.Contains("<Border Grid.Row=\"3\" x:Name=\"ApplyReadinessBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ApplyReadinessHeadlineText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ApplyReadinessDetailText\"", xaml, StringComparison.Ordinal);
        // 就绪条在动作行之前：先说清能不能做、缺什么，再给按钮。
        Assert.Contains("<Grid Grid.Row=\"4\" x:Name=\"LayoutActionRow\" ColumnSpacing=\"8\">", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockReasons_LiveInTheSharedDecisionNotInTheShell()
    {
        var root = FindRepositoryRoot();
        var decision = Path.Combine(root, "src", "DesktopAgent.Application", "DesktopLayouts", "Workspaces", "DesktopApplyReadiness.cs");
        Assert.True(File.Exists(decision), "判定必须住在 Application 里，才能被单测穷举。");

        var offenders = new StringBuilder();
        foreach (var file in Directory.GetFiles(
                     Path.Combine(root, "src", "DesktopAgent.WinUI"),
                     "LayoutManagementView*.cs"))
        {
            if (File.ReadAllText(file).Contains("还不能应用", StringComparison.Ordinal))
                offenders.Append(Path.GetFileName(file)).Append(' ');
        }

        Assert.True(offenders.Length == 0, $"界面里又出现了第二份理由文案：{offenders}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DesktopAgent.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
