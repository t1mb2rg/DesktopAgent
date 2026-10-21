using DesktopAgent.Application.DesktopLayouts.Workspaces;

namespace DesktopAgent.Tests.Productization;

/// <summary>
/// 「应用到桌面」能不能点，此前散在 6 个界面 partial 的 18 处写入里，也没人守。
/// 这里守的是抽出来的唯一判定：它是纯函数，所以可以穷举。
/// </summary>
public sealed class DesktopApplyReadinessTests
{
    [Fact]
    public void NoPreview_BlocksWithNoPreviewReason()
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.None,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: false,
            FreshPreviewMoveRequiredCount: 0,
            HasFrozenWorkspaceApplyTicket: false,
            FrozenWorkspaceMoveCount: 0));

        Assert.False(readiness.CanApply);
        Assert.Equal(DesktopApplyBlockReason.NoPreview, readiness.Reason);
        Assert.Equal("应用到桌面", readiness.ButtonText);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Headline));
        Assert.False(string.IsNullOrWhiteSpace(readiness.Detail));
    }

    [Fact]
    public void FreshPreview_CompleteWithMoves_AllowsApply()
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.Fresh,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: true,
            FreshPreviewMoveRequiredCount: 7,
            HasFrozenWorkspaceApplyTicket: false,
            FrozenWorkspaceMoveCount: 0));

        Assert.True(readiness.CanApply);
        Assert.Equal(DesktopApplyBlockReason.None, readiness.Reason);
        Assert.Equal("应用到桌面", readiness.ButtonText);
        // 「可以应用」也要说清代价：这次要移动几项。
        Assert.Contains("7", readiness.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 3, DesktopApplyBlockReason.PreviewIncomplete)]
    [InlineData(true, 0, DesktopApplyBlockReason.NoMoveRequired)]
    public void FreshPreview_IncompleteOrNothingToMove_Blocks(
        bool isComplete,
        int moveRequiredCount,
        DesktopApplyBlockReason expectedReason)
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.Fresh,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: isComplete,
            FreshPreviewMoveRequiredCount: moveRequiredCount,
            HasFrozenWorkspaceApplyTicket: false,
            FrozenWorkspaceMoveCount: 5));

        Assert.False(readiness.CanApply);
        Assert.Equal(expectedReason, readiness.Reason);
    }

    [Fact]
    public void FrozenLogicalPreview_IsReadOnlyEvenWithEverythingElseGreen()
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.FrozenLogical,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: true,
            FreshPreviewMoveRequiredCount: 9,
            HasFrozenWorkspaceApplyTicket: true,
            FrozenWorkspaceMoveCount: 9));

        Assert.False(readiness.CanApply);
        Assert.Equal(DesktopApplyBlockReason.FrozenLogicalPreviewIsReadOnly, readiness.Reason);
    }

    [Theory]
    [InlineData(false, 9, DesktopApplyBlockReason.FrozenWorkspaceTicketUnavailable)]
    [InlineData(true, 0, DesktopApplyBlockReason.NoMoveRequired)]
    public void FrozenWorkspace_RequiresLiveTicketAndSomethingToMove(
        bool hasTicket,
        int moveCount,
        DesktopApplyBlockReason expectedReason)
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.FrozenWorkspace,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: false,
            FreshPreviewMoveRequiredCount: 0,
            HasFrozenWorkspaceApplyTicket: hasTicket,
            FrozenWorkspaceMoveCount: moveCount));

        Assert.False(readiness.CanApply);
        Assert.Equal(expectedReason, readiness.Reason);
    }

    [Fact]
    public void FrozenWorkspace_WithTicketAndMoves_AllowsApplyAndRenamesButton()
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.FrozenWorkspace,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: false,
            FreshPreviewMoveRequiredCount: 0,
            HasFrozenWorkspaceApplyTicket: true,
            FrozenWorkspaceMoveCount: 3));

        Assert.True(readiness.CanApply);
        Assert.Equal("应用这个布局", readiness.ButtonText);
    }

    [Fact]
    public void ExecuteInProgress_OutranksEveryEvidenceBlocker()
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.Fresh,
            ExecuteInProgress: true,
            FreshPreviewIsComplete: true,
            FreshPreviewMoveRequiredCount: 7,
            HasFrozenWorkspaceApplyTicket: true,
            FrozenWorkspaceMoveCount: 7,
            BlockOverride: DesktopApplyBlockReason.PreviewNotExecutable));

        Assert.False(readiness.CanApply);
        Assert.Equal(DesktopApplyBlockReason.ExecuteInProgress, readiness.Reason);
    }

    [Fact]
    public void EvidenceBoundOverride_WinsOverAnOtherwiseAllowedPreview()
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.Fresh,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: true,
            FreshPreviewMoveRequiredCount: 7,
            HasFrozenWorkspaceApplyTicket: false,
            FrozenWorkspaceMoveCount: 0,
            BlockOverride: DesktopApplyBlockReason.PendingRecovery));

        Assert.False(readiness.CanApply);
        Assert.Equal(DesktopApplyBlockReason.PendingRecovery, readiness.Reason);
        // 阻断期间按钮文案不来回跳：仍然是这条路径的名字。
        Assert.Equal("应用到桌面", readiness.ButtonText);
    }

    [Fact]
    public void NoneOverride_IsNotABlock()
    {
        var readiness = Evaluate(new DesktopApplyReadinessInputs(
            DesktopApplyPreviewKind.Fresh,
            ExecuteInProgress: false,
            FreshPreviewIsComplete: true,
            FreshPreviewMoveRequiredCount: 2,
            HasFrozenWorkspaceApplyTicket: false,
            FrozenWorkspaceMoveCount: 0,
            BlockOverride: DesktopApplyBlockReason.None));

        Assert.True(readiness.CanApply);
        Assert.Equal(DesktopApplyBlockReason.None, readiness.Reason);
    }

    [Fact]
    public void Exhaustive_ApplyIsOpenOnlyForCompleteFreshPreviewOrTicketedFrozenWorkspace()
    {
        var openCount = 0;

        foreach (var kind in Enum.GetValues<DesktopApplyPreviewKind>())
        {
            foreach (var executeInProgress in new[] { false, true })
            {
                foreach (var isComplete in new[] { false, true })
                {
                    foreach (var moveCount in new[] { 0, 3 })
                    {
                        foreach (var hasTicket in new[] { false, true })
                        {
                            foreach (var frozenMoveCount in new[] { 0, 3 })
                            {
                                foreach (var overridden in new[]
                                {
                                    (DesktopApplyBlockReason?)null,
                                    DesktopApplyBlockReason.None,
                                    DesktopApplyBlockReason.PendingRecovery,
                                })
                                {
                                    var readiness = Evaluate(new DesktopApplyReadinessInputs(
                                        kind,
                                        executeInProgress,
                                        isComplete,
                                        moveCount,
                                        hasTicket,
                                        frozenMoveCount,
                                        overridden));

                                    var allowed = !executeInProgress &&
                                        (overridden is null || overridden == DesktopApplyBlockReason.None) &&
                                        (kind == DesktopApplyPreviewKind.Fresh && isComplete && moveCount > 0 ||
                                         kind == DesktopApplyPreviewKind.FrozenWorkspace && hasTicket && frozenMoveCount > 0);

                                    Assert.Equal(allowed, readiness.CanApply);

                                    if (readiness.CanApply)
                                    {
                                        openCount++;
                                        Assert.Equal(DesktopApplyBlockReason.None, readiness.Reason);
                                        continue;
                                    }

                                    // 关着的时候必须有理由，而且理由要说得出下一步怎么办。
                                    Assert.NotEqual(DesktopApplyBlockReason.None, readiness.Reason);
                                    Assert.False(string.IsNullOrWhiteSpace(readiness.Headline));
                                    Assert.False(string.IsNullOrWhiteSpace(readiness.Detail));
                                }
                            }
                        }
                    }
                }
            }
        }

        // 防止这条穷举在「没有任何组合能打开」时变成空转。
        Assert.True(openCount > 0, "全组合遍历没有覆盖到任何可应用结果，性质检查等于没有运行。");
    }

    private static DesktopApplyReadiness Evaluate(DesktopApplyReadinessInputs inputs)
        => inputs.Evaluate();
}
