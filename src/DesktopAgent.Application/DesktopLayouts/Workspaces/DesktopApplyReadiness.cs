namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>
/// 当前预览来自哪条路径。三条路径的可写性不同，不能混为一谈：
/// 只有 fresh 直接预览与「AI 工作区精确预览（Frozen Preview）」允许落地，
/// AI 逻辑终态冻结预览只用来审阅。
/// </summary>
public enum DesktopApplyPreviewKind
{
    /// <summary>还没有任何预览。</summary>
    None = 0,

    /// <summary>刚刚 fresh 读过当前桌面的直接应用预览。</summary>
    Fresh = 1,

    /// <summary>AI 精确逻辑终态冻结预览：只读，没有写入入口。</summary>
    FrozenLogical = 2,

    /// <summary>AI 工作区精确预览：凭一次性凭据可以写。</summary>
    FrozenWorkspace = 3,
}

/// <summary>
/// 「现在为什么不能应用到桌面」的唯一枚举。
/// </summary>
public enum DesktopApplyBlockReason
{
    /// <summary>可以应用。</summary>
    None = 0,

    /// <summary>一次执行还没彻底结束（包括恢复协调占用）。</summary>
    ExecuteInProgress = 1,

    /// <summary>还没有预览。</summary>
    NoPreview = 2,

    /// <summary>手上这份逻辑终态只能看，不能写。</summary>
    FrozenLogicalPreviewIsReadOnly = 3,

    /// <summary>预览不完整（没有覆盖全部要做的事）。</summary>
    PreviewIncomplete = 4,

    /// <summary>按当前预览不需要移动任何项目。</summary>
    NoMoveRequired = 5,

    /// <summary>精确预览的一次性凭据已经消费或失效。</summary>
    FrozenWorkspaceTicketUnavailable = 6,

    /// <summary>计划本身不可执行（生成安全执行候选时被拒）。</summary>
    PreviewNotExecutable = 7,

    /// <summary>存在未收敛的恢复现场。</summary>
    PendingRecovery = 8,

    /// <summary>刚才那次预览失败了。</summary>
    PreviewAttemptFailed = 9,
}

/// <summary>
/// 判定「应用到桌面」能不能点的全部输入。全部是界面当下就有的事实，
/// 不读盘、不产生副作用——所以它可以被穷举测试。
/// </summary>
public readonly record struct DesktopApplyReadinessInputs(
    DesktopApplyPreviewKind PreviewKind,
    bool ExecuteInProgress,
    bool FreshPreviewIsComplete,
    int FreshPreviewMoveRequiredCount,
    bool HasFrozenWorkspaceApplyTicket,
    int FrozenWorkspaceMoveCount,
    DesktopApplyBlockReason? BlockOverride = null)
{
    /// <summary>按钮叫什么，取决于手上是哪条路径的预览。阻断时也保持不变，避免文案来回跳。</summary>
    private string ButtonText => PreviewKind == DesktopApplyPreviewKind.FrozenWorkspace
        ? "应用这个布局"
        : "应用到桌面";

    /// <summary>
    /// 唯一判定。结果同时给出按钮文案和「为什么」的说明——
    /// 这样界面不需要在别处再猜一遍原因。
    /// </summary>
    public DesktopApplyReadiness Evaluate()
    {
        if (ExecuteInProgress)
            return Blocked(
                DesktopApplyBlockReason.ExecuteInProgress,
                "正在执行 · 结束前不能再次应用",
                "等这次执行彻底结束（包括恢复协调）再点。执行期间不接受第二次应用。");

        if (BlockOverride is { } overridden && overridden != DesktopApplyBlockReason.None)
            return Describe(overridden);

        return PreviewKind switch
        {
            DesktopApplyPreviewKind.None => Blocked(
                DesktopApplyBlockReason.NoPreview,
                "还不能应用 · 没有预览",
                "先点「↻ 预览」读一次当前桌面：这里只执行你刚看过并确认的结果。"),

            DesktopApplyPreviewKind.FrozenLogical => Blocked(
                DesktopApplyBlockReason.FrozenLogicalPreviewIsReadOnly,
                "还不能应用 · 这份逻辑终态只能看",
                "AI 逻辑终态冻结预览没有写入权限。要落地就回到 AI 助手生成精确预览。"),

            DesktopApplyPreviewKind.FrozenWorkspace => EvaluateFrozenWorkspace(),

            _ => EvaluateFreshPreview(),
        };
    }

    private DesktopApplyReadiness EvaluateFreshPreview()
    {
        if (!FreshPreviewIsComplete)
            return Blocked(
                DesktopApplyBlockReason.PreviewIncomplete,
                "还不能应用 · 预览不完整",
                "这次预览没有覆盖全部要做的事。先解决预览里报出的问题，再重新预览。");

        if (FreshPreviewMoveRequiredCount <= 0)
            return Blocked(
                DesktopApplyBlockReason.NoMoveRequired,
                "不需要应用",
                "按当前预览，桌面已经是目标样子（要移动 0 项）。");

        return new DesktopApplyReadiness(
            true,
            ButtonText,
            DesktopApplyBlockReason.None,
            "可以应用",
            $"这次预览要移动 {FreshPreviewMoveRequiredCount} 项。点击后会重新 fresh 验证，再交给安全执行层写入。");
    }

    private DesktopApplyReadiness EvaluateFrozenWorkspace()
    {
        if (!HasFrozenWorkspaceApplyTicket)
            return Blocked(
                DesktopApplyBlockReason.FrozenWorkspaceTicketUnavailable,
                "还不能应用 · 精确预览的一次性凭据已失效",
                "这份 Frozen Preview 只保留作审阅。回到 AI 助手重新生成精确预览后，应用入口才会重新开放。");

        if (FrozenWorkspaceMoveCount <= 0)
            return Blocked(
                DesktopApplyBlockReason.NoMoveRequired,
                "不需要应用",
                "这份精确预览里没有需要移动的项目。");

        return new DesktopApplyReadiness(
            true,
            ButtonText,
            DesktopApplyBlockReason.None,
            "可以应用 · 精确预览尚未消费",
            $"这份 Frozen Preview 要移动 {FrozenWorkspaceMoveCount} 项。点击后会先做 fresh check，再交给安全执行层写入。");
    }

    private DesktopApplyReadiness Describe(DesktopApplyBlockReason reason)
        => reason switch
        {
            DesktopApplyBlockReason.PreviewNotExecutable => Blocked(
                reason,
                "还不能应用 · 当前计划不可执行",
                "上面报出的问题解决之前，应用入口保持关闭。改完请重新预览，让证据重新成立。"),

            DesktopApplyBlockReason.PendingRecovery => Blocked(
                reason,
                "还不能应用 · 有未处理的恢复现场",
                "存在没有安全收敛的执行现场，恢复之前不开放新的写入。点「处理恢复状态」。"),

            DesktopApplyBlockReason.PreviewAttemptFailed => Blocked(
                reason,
                "还不能应用 · 刚才没能生成预览",
                "预览失败期间不开放写入。修好上面报出的问题，重新点「↻ 预览」。"),

            _ => Blocked(
                reason,
                "还不能应用",
                "当前证据不足以开放写入。纠正上面报出的问题后重新预览。"),
        };

    private DesktopApplyReadiness Blocked(
        DesktopApplyBlockReason reason,
        string headline,
        string detail)
        => new(false, ButtonText, reason, headline, detail);
}

/// <summary>
/// 就绪条的结论：能不能应用、按钮叫什么、为什么（一句话 + 一句「怎么办」）。
/// </summary>
public sealed record DesktopApplyReadiness(
    bool CanApply,
    string ButtonText,
    DesktopApplyBlockReason Reason,
    string Headline,
    string Detail);
