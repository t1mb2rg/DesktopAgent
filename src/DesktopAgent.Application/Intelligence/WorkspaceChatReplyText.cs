using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

namespace DesktopAgent.Application.Intelligence;

/// <summary>工具响应没有自然文字时，使用已校验动作生成具体反馈；只在本地提交成功后展示。</summary>
public static class WorkspaceChatReplyText
{
    public static string ForCommittedReply(WorkspaceChatReply reply)
    {
        if (!string.IsNullOrWhiteSpace(reply.Text)) return reply.Text;
        var summary = reply.Commands.OfType<SetWorkspaceSummary>().LastOrDefault()?.Summary;
        var zones = reply.Commands.OfType<UpsertWorkspaceZone>().Select(command => command.Zone.Name).Distinct().ToArray();
        if (!string.IsNullOrWhiteSpace(summary))
            return $"{summary.Trim()}\n\n可以在设计画布查看调整后的效果，桌面图标尚未移动。";
        if (zones.Length > 0)
            return $"已调整这些分区：{string.Join("、", zones)}。可以在设计画布查看效果，继续告诉我哪里需要调整。";
        if (reply.Commands.Count > 0)
            return "已按你的要求调整分区规则，可以在设计画布查看当前方案。";
        return reply.Action?.Title ?? "可以继续告诉我你想怎样调整桌面。";
    }
}
