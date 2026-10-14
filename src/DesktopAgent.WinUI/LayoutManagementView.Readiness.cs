using DesktopAgent.Application.DesktopLayouts.Workspaces;
using Microsoft.UI.Xaml;

namespace DesktopAgent.WinUI;

/// <summary>
/// 「应用到桌面」为什么能点、为什么点不动——唯一判定入口与唯一写按钮的地方。
///
/// 判定本身是纯函数（<see cref="DesktopApplyReadinessInputs.Evaluate"/>，落在 Application 里，可穷举单测）。
/// 这个文件只做两件事：把界面当下的事实翻译成输入；把结论写进按钮和就绪条。
/// 其它 partial 不许再直接改 ApplyToDesktopButton 的状态——
/// tests/DesktopAgent.Tests/Productization/ProductApplyEntryContractTests.cs 会拦住。
/// </summary>
public sealed partial class LayoutManagementView
{
    /// <summary>
    /// 纯事实表达不了的阻断（例如"计划不可执行"只有在生成安全执行候选之后才知道）。
    /// 它只对当次证据有效：换一份预览、或证据被作废，就自动不再生效。
    /// </summary>
    private DesktopApplyBlockReason? _applyBlockOverride;
    private object? _applyBlockOverrideEvidence;

    private void RefreshApplyButtonFromCurrentEvidence()
    {
        var readiness = BuildApplyReadinessInputs().Evaluate();

        ApplyToDesktopButton.Content = readiness.ButtonText;
        ApplyToDesktopButton.IsEnabled = readiness.CanApply;

        ApplyReadinessHeadlineText.Text = readiness.Headline;
        ApplyReadinessDetailText.Text = readiness.Detail;
        ApplyReadinessGlyphText.Text = readiness.CanApply ? "\uE73E" : "\uE7BA";
    }

    /// <summary>把"只有当次证据成立"的阻断理由记下来，并立刻重算。</summary>
    private void BlockApplyForCurrentEvidence(DesktopApplyBlockReason reason)
    {
        _applyBlockOverride = reason;
        _applyBlockOverrideEvidence = _latestPreview;
        RefreshApplyButtonFromCurrentEvidence();
    }

    private DesktopApplyReadinessInputs BuildApplyReadinessInputs()
        => new(
            PreviewKind: ResolveApplyPreviewKind(),
            ExecuteInProgress: _directApplyInProgress,
            FreshPreviewIsComplete: _latestPreview?.Plan.IsComplete == true,
            FreshPreviewMoveRequiredCount: _latestPreview?.Plan.MoveRequiredCount ?? 0,
            HasFrozenWorkspaceApplyTicket: _workspaceFrozenPreview is not null
                && HasWorkspaceFrozenPreviewApplyTicket(_workspaceFrozenPreview),
            FrozenWorkspaceMoveCount: _workspaceFrozenPreview?.MoveCount ?? 0,
            BlockOverride: CurrentApplyBlockOverride());

    private DesktopApplyPreviewKind ResolveApplyPreviewKind()
    {
        if (_workspaceFrozenPreview is not null)
            return DesktopApplyPreviewKind.FrozenWorkspace;

        if (_logicalWorkspaceSession is not null)
            return DesktopApplyPreviewKind.FrozenLogical;

        return _latestPreview is not null
            ? DesktopApplyPreviewKind.Fresh
            : DesktopApplyPreviewKind.None;
    }

    private DesktopApplyBlockReason? CurrentApplyBlockOverride()
        => _applyBlockOverride is not null
            && ReferenceEquals(_applyBlockOverrideEvidence, _latestPreview)
            && _latestPreview is not null
                ? _applyBlockOverride
                : null;
}
