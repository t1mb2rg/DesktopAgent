using System.IO;

namespace DesktopAgent.Tests.Productization;

/// <summary>
/// 「撤销上次整理」的产品边界（源码级断言）。
///
/// 撤销是**写桌面**的动作，所以它必须和「应用到桌面」共用同一条受控执行路径：
/// 产品层只负责给入口、先做只读核对、再弹确认；真正的写入判定与执行在现场服务里。
/// </summary>
public sealed class ProductRollbackEntryContractTests
{
    [Fact]
    public void ProductLayoutSurface_ExposesRollbackEntryNextToRecovery()
    {
        var xaml = ReadWinUiFile("LayoutManagementView.xaml");

        Assert.Contains("x:Name=\"RollbackLastApplyButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RollbackLastApply_Click\"", xaml, StringComparison.Ordinal);
        // 入口要和「处理恢复状态」放在一起：这两件事都是"执行现场"的操作。
        Assert.Contains("x:Name=\"ReconcileRecoveryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("撤销上次整理", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RollbackPartial_ChecksReadOnlyPreflightBeforeExecuting()
    {
        var partial = ReadWinUiFile("LayoutManagementView.Rollback.cs");

        var preflightIndex = partial.IndexOf("PrepareAsync()", StringComparison.Ordinal);
        var rollbackIndex = partial.IndexOf("RollbackAsync()", StringComparison.Ordinal);

        Assert.True(preflightIndex >= 0, "撤销入口必须先做只读核对（PrepareAsync）。");
        Assert.True(rollbackIndex >= 0, "撤销入口必须走服务的受控执行入口（RollbackAsync）。");
        Assert.True(
            preflightIndex < rollbackIndex,
            "只读核对必须发生在写入之前：顺序反了就等于先动桌面再问为什么。");

        // 核对不通过就停住并说明原因，不进入执行。
        Assert.Contains("if (!preflight.CanRollback)", partial, StringComparison.Ordinal);
        // 执行前必须有一次明确的用户确认。
        Assert.Contains("ConfirmRollbackAsync", partial, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonText = \"撤销\"", partial, StringComparison.Ordinal);
        // 撤销改动了桌面：必须让冻结预览证据失效，逼用户重新预览。
        Assert.Contains("InvalidateDirectApplyEvidence(", partial, StringComparison.Ordinal);
        // 页面自己的状态条也要如实记录这次撤销（不能撤销后还挂着"已应用到桌面"）。
        Assert.Contains("ShowInfo(\"已撤销上次整理\"", partial, StringComparison.Ordinal);
    }

    [Fact]
    public void RollbackPartial_NeverTalksToTheShellWriterOrComDirectly()
    {
        var partial = ReadWinUiFile("LayoutManagementView.Rollback.cs");

        foreach (var forbidden in new[]
                 {
                     "DesktopShellIconPositionWriter",
                     "IDesktopIconPositionWriter",
                     "IFolderView",
                     "SelectAndPositionItems",
                     "System.Runtime.InteropServices",
                 })
        {
            Assert.DoesNotContain(forbidden, partial, StringComparison.Ordinal);
        }

        // 服务由产品层装配：复用与直接应用同一批存储（安全检查点 + 恢复会话）。
        Assert.Contains("new DesktopLayoutRollbackService(", partial, StringComparison.Ordinal);
        Assert.Contains("_directApplySnapshotStore", partial, StringComparison.Ordinal);
        Assert.Contains("_directApplyRecoveryStore", partial, StringComparison.Ordinal);
    }

    [Fact]
    public void RollbackService_ReusesTheDirectApplyExecutionPath()
    {
        var service = ReadFile(Path.Combine(
            "src",
            "DesktopAgent.WindowsInterop",
            "DesktopLayouts",
            "Restore",
            "Execution",
            "DesktopLayoutRollbackService.cs"));

        // 与「应用到桌面」同一套受控执行入口与同一道安全门。
        Assert.Contains("TargetDisplayLayoutExecutionCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch", service, StringComparison.Ordinal);
        Assert.Contains("TargetDisplayLayoutStagingExecutionScopeGate.Evaluate", service, StringComparison.Ordinal);
        // 还原目标来自安全检查点，而不是"反向挪一遍"。
        Assert.Contains("TargetDisplayLayoutRestorePlanner.CreatePlan", service, StringComparison.Ordinal);
        Assert.Contains("_snapshotStore.GetAsync(candidate.SafetySnapshotId", service, StringComparison.Ordinal);
        // 两条执行路径都要看得见：直接应用写恢复会话，工作区设计应用只写安全检查点 + 动态 journal。
        // 只认一条会让「撤销上次整理」撤不掉另一条，还会错误地退回到更早的那次。
        Assert.Contains("ICommittedWorkspaceExecutionJournalQuery", service, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutRollbackCandidate.TryFromRecoverySession", service, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutRollbackCandidate.TryFromWorkspaceApply", service, StringComparison.Ordinal);
        // 未收敛现场一律先停。
        Assert.Contains("ListUnresolvedAsync", service, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutRollbackBlockReason.UnresolvedRecoverySite", service, StringComparison.Ordinal);
    }

    private static string ReadWinUiFile(string fileName)
        => ReadFile(Path.Combine("src", "DesktopAgent.WinUI", fileName));

    private static string ReadFile(string relativePath)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));

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
