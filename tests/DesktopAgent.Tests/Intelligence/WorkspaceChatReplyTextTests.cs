using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Tests.Intelligence;

public sealed class WorkspaceChatReplyTextTests
{
    [Fact]
    public void NaturalReplyIsPreserved()
        => Assert.Equal("这样更适合竖屏，我改成横条了。", WorkspaceChatReplyText.ForCommittedReply(
            new("这样更适合竖屏，我改成横条了。", [new SetWorkspaceSummary("summary")], null)));

    [Fact]
    public void ToolOnlyReplyUsesActualDesignSummary()
    {
        var text = WorkspaceChatReplyText.ForCommittedReply(new("", [new SetWorkspaceSummary("左侧副屏使用六条横向分区")], null));
        Assert.Contains("六条横向分区", text);
        Assert.Contains("尚未移动", text);
        Assert.DoesNotContain("已更新设计草稿", text);
    }

    [Fact]
    public void ToolOnlyReplyNamesChangedZonesInsteadOfGenericAcknowledgement()
    {
        var zone = new DesktopWorkspaceBlueprintZone("work", "工作开发", "primary", new(0, 0, 1, 0.3));
        Assert.Contains("工作开发", WorkspaceChatReplyText.ForCommittedReply(new(" ", [new UpsertWorkspaceZone(zone)], null)));
    }

    [Fact]
    public void ActionOnlyReplyUsesTheProposedActionTitle()
        => Assert.Equal("保存为竖屏方案？", WorkspaceChatReplyText.ForCommittedReply(
            new("", [], new(WorkspaceChatActionKind.SaveLayout, "保存为竖屏方案？", "竖屏方案"))));
}
