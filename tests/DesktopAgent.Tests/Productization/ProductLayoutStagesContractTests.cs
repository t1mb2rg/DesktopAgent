using DesktopAgent.Application.DesktopLayouts.Workspaces;

namespace DesktopAgent.Tests.Productization;

/// <summary>
/// L2（面板三阶段）：动作按阶段分家，一步只做一件事。
/// 两条不许破的性质：
/// ① 阶段表是可见性的唯一出处，界面不自己判断"该藏什么"；
/// ② 分阶段不制造第二个门槛 —— "能不能应用"仍然只有 <c>DesktopApplyReadiness</c> 一处判定。
/// </summary>
public sealed class ProductLayoutStagesContractTests
{
    /// <summary>把机器准备好/换回来才需要按的按钮：它们属于「准备」，不属于执行动作行。</summary>
    private static readonly string[] MachineStepButtonNames =
    [
        "GridProbeButton",
        "GridDisplayAuthorizationsButton",
        "ReconcileRecoveryButton",
        "OverlayToggleButton",
    ];

    [Fact]
    public void StageTable_IsExactlyPrepareDesignExecute()
        => Assert.Equal(
            new[] { DesktopLayoutStage.Prepare, DesktopLayoutStage.Design, DesktopLayoutStage.Execute },
            DesktopLayoutStagePresentation.Stages);

    [Fact]
    public void EveryGatedName_ExistsInTheSurface_AndIsUsedByTheStageCode()
    {
        var stages = Read("src", "DesktopAgent.WinUI", "LayoutManagementView.Stages.cs");
        var xaml = Read("src", "DesktopAgent.WinUI", "LayoutManagementView.xaml");

        foreach (var name in DesktopLayoutStagePresentation.GatedElementNames)
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
            Assert.Contains($"\"{name}\"", stages, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnknownElement_IsRejected_NotSilentlyHidden()
    {
        // 阶段表没登记的控件名必须当场报错：静默返回 false 会把控件悄悄藏掉，
        // 而"看不见的入口"正是这一轮要修的毛病。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopLayoutStagePresentation.IsVisibleInStage(DesktopLayoutStage.Design, "NotGatedAnywhere"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopLayoutStagePresentation.IsVisibleInStage((DesktopLayoutStage)99, "LayoutCanvasCard"));
    }

    [Fact]
    public void EveryStage_ExplainsItself_AndMarksTheActiveOne()
    {
        foreach (var stage in DesktopLayoutStagePresentation.Stages)
        {
            Assert.False(string.IsNullOrWhiteSpace(DesktopLayoutStagePresentation.Title(stage)));
            Assert.False(string.IsNullOrWhiteSpace(DesktopLayoutStagePresentation.Hint(stage)));
            Assert.DoesNotContain(
                "●",
                DesktopLayoutStagePresentation.StageButtonContent(stage, isActive: false),
                StringComparison.Ordinal);
            Assert.Contains(
                "●",
                DesktopLayoutStagePresentation.StageButtonContent(stage, isActive: true),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StageCode_DoesNotTouchTheApplyGate()
    {
        var stages = Read("src", "DesktopAgent.WinUI", "LayoutManagementView.Stages.cs");

        // 分阶段最容易犯的错，就是在切阶段时顺手把"能不能应用"再判一遍。
        Assert.DoesNotContain("ApplyToDesktopButton", stages, StringComparison.Ordinal);
        Assert.DoesNotContain(".Evaluate(", stages, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopApplyBlockReason", stages, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutStagePresentation.IsVisibleInStage", stages, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteStage_HasOnlyTheThreeApplySteps_AndPrepareStage_HoldsTheMachineSteps()
    {
        var xaml = Read("src", "DesktopAgent.WinUI", "LayoutManagementView.xaml");

        var stageBar = Slice(xaml, "x:Name=\"LayoutStageBar\"", "</Border>");
        foreach (var button in new[] { "StagePrepareButton", "StageDesignButton", "StageExecuteButton" })
            Assert.Contains($"x:Name=\"{button}\"", stageBar, StringComparison.Ordinal);
        Assert.Contains("StageHintText", stageBar, StringComparison.Ordinal);

        var actionRow = Slice(xaml, "x:Name=\"LayoutActionRow\"", "</Grid>");
        Assert.Equal(3, Count(actionRow, "<Button"));
        Assert.Contains("PreviewLayoutButton", actionRow, StringComparison.Ordinal);
        Assert.Contains("ApplyToDesktopButton", actionRow, StringComparison.Ordinal);
        Assert.Contains("SaveLayoutButton", actionRow, StringComparison.Ordinal);

        var preparePanel = Slice(xaml, "x:Name=\"PrepareStagePanel\"", "</Border>");
        foreach (var name in MachineStepButtonNames)
            Assert.Contains($"x:Name=\"{name}\"", preparePanel, StringComparison.Ordinal);

        // 一次只做一件事：机器类动作不许再出现在执行动作行里。
        foreach (var name in MachineStepButtonNames)
            Assert.DoesNotContain(name, actionRow, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareStage_SaysWhatMustBeTrueBeforeUnattendedExecution()
    {
        var xaml = Read("src", "DesktopAgent.WinUI", "LayoutManagementView.xaml");
        var preparePanel = Slice(xaml, "x:Name=\"PrepareStagePanel\"", "</Border>");

        Assert.Contains("无人值守执行的前提只有两件", preparePanel, StringComparison.Ordinal);
        Assert.Contains("实测", preparePanel, StringComparison.Ordinal);
        Assert.Contains("站定授权", preparePanel, StringComparison.Ordinal);
        Assert.Contains("需要重新校准", preparePanel, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"界面里找不到 {from}。");

        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"从 {from} 之后找不到 {to}。");

        return text[start..end];
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
