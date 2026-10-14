namespace DesktopAgent.Tests.Productization;

/// <summary>
/// 执行面收口：自动执行只能落在**一块**目标屏上。
/// 来源可以是任意多块屏（跨屏搬迁照旧合法），但目标不唯一时，没有任何一块屏的实测网格
/// 与站定授权能覆盖整个动作，所以这种 Plan 必须被判为不合法，而不是靠调用方自觉收口。
/// </summary>
public sealed class ProductSingleTargetDisplayContractTests
{
    [Fact]
    public void PlanValidator_RejectsPlansThatTargetMoreThanOneDisplay()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.Core",
            "DesktopLayouts",
            "Workspaces",
            "DesktopDesiredWorkspacePlanValidator.cs"));

        Assert.Contains("MultipleTargetDisplays = 25", source, StringComparison.Ordinal);
        Assert.Contains("目标必须唯一", source, StringComparison.Ordinal);
        Assert.Contains("自动执行只允许一块目标屏", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalWorkspacePreview_ShowsTheSingleTargetDisplay_NotTheSourceSpread()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.LogicalWorkspacePreview.cs"));

        // 旧文案把「逻辑工作区跨了几块屏」当成目标屏，正是收口要消掉的那种混淆。
        Assert.DoesNotContain("多屏逻辑工作区", source, StringComparison.Ordinal);
        Assert.Contains("DesktopDesiredItemState.Place", source, StringComparison.Ordinal);
        Assert.Contains("不合法：自动执行只允许一块", source, StringComparison.Ordinal);
        Assert.Contains("保持现状（不移动任何图标）", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductLayoutSurface_KeepsASingleTargetDisplayTile()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.xaml"));

        Assert.Contains("x:Name=\"TargetDisplayText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("目标屏", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductLayoutSurface_StatesTheSingleTargetDisplayBoundaryBeforeTheAction()
    {
        // 边界要写在动作之前，而不是等计划被拒了才出现。
        // 措辞必须说成能力形状（多屏可用 + 逐屏校准 + 单目标执行），不能写成"只支持单屏"。
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.xaml"));

        Assert.Contains("一次执行只落这一块屏", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "多屏可用：来源可以是任何屏；一次自动执行只落在一块目标屏，其他屏保持现状",
            xaml,
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
