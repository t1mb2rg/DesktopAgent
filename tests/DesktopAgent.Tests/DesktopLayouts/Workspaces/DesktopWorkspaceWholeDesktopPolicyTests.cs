using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopWorkspaceWholeDesktopPolicyTests
{
    [Fact]
    public void EmptyPrimaryStillParticipatesSoItsIconsCanMoveOut()
    {
        var original = new DesktopWorkspaceDesignDraft(Guid.NewGuid(), "主屏清空",
            zones: [new("work", "工作", "secondary_1", new(0, 0, 1, 1)), new("inbox", "待确认", "secondary_2", new(0, 0, 1, 1))]);
        var completed = DesktopWorkspaceWholeDesktopPolicy.CompleteIfAbsent(original, ["primary", "secondary_1", "secondary_2"]);
        Assert.Equal(3, completed.ToBlueprint().DisplayPolicies.Count);
        Assert.All(completed.DisplayPolicies, policy => Assert.Equal(DesktopWorkspaceDisplayPolicyKind.Design, policy.Policy));
        Assert.DoesNotContain(completed.Zones, zone => zone.DisplayRef == "primary");
        Assert.Empty(original.DisplayPolicies);
        Assert.Equal(original.Zones, completed.Zones);
    }

    [Fact]
    public void ExistingPreserveAndPartialPoliciesAreNeverSilentlyExpanded()
    {
        var original = new DesktopWorkspaceDesignDraft(Guid.NewGuid(), displayPolicies:
            [new("primary", DesktopWorkspaceDisplayPolicyKind.Preserve)]);
        Assert.Same(original, DesktopWorkspaceWholeDesktopPolicy.CompleteIfAbsent(original, ["primary", "secondary_1"]));
    }

    [Fact]
    public void MissingScreenIsNotGuessedOrRenamed()
    {
        var original = new DesktopWorkspaceDesignDraft(Guid.NewGuid(), zones: [new("work", "工作", "secondary_2", new(0, 0, 1, 1))]);
        Assert.Throws<InvalidOperationException>(() => DesktopWorkspaceWholeDesktopPolicy.CompleteIfAbsent(original, ["primary"]));
    }

    [Fact]
    public void DisplayPolicyCanBeEditedAndUndoneAtomically()
    {
        var session = new DesktopWorkspaceDesignSession();
        Assert.True(session.TryApply(0, [new SetWorkspaceDisplayPolicy(new("primary", DesktopWorkspaceDisplayPolicyKind.Design))]).Applied);
        Assert.True(session.TryApply(1, [new SetWorkspaceDisplayPolicy(new("primary", DesktopWorkspaceDisplayPolicyKind.Preserve))]).Applied);
        Assert.Equal(DesktopWorkspaceDisplayPolicyKind.Preserve, Assert.Single(session.Snapshot().State.Draft.DisplayPolicies).Policy);
        Assert.True(session.Undo(2).Applied);
        Assert.Equal(DesktopWorkspaceDisplayPolicyKind.Design, Assert.Single(session.Snapshot().State.Draft.DisplayPolicies).Policy);
    }
}
