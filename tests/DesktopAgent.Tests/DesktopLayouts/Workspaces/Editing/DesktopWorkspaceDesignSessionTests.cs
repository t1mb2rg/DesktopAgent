using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Editing;

public sealed class DesktopWorkspaceDesignSessionTests
{
    [Fact]
    public void SemanticRefresh_InvalidatesOldRepliesWithoutDiscardingDesignOrUndo()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new SetWorkspaceSummary("keep this")]);
        session.InvalidateExternalContext();
        Assert.Equal("keep this", session.Snapshot().State.Draft.Summary);
        Assert.Equal(DesktopWorkspaceEditStatus.Stale, session.TryApply(1, [new SetWorkspaceSummary("late")]).Status);
        Assert.True(session.Undo(2).Applied);
        Assert.Empty(session.Snapshot().State.Draft.Summary);
    }

    [Fact]
    public void NewSession_IsIncompleteWithoutPretendingToBeExecutable()
    {
        var snapshot = new DesktopWorkspaceDesignSession().Snapshot();
        Assert.Empty(snapshot.State.Draft.Zones);
        Assert.Throws<InvalidOperationException>(() => snapshot.State.Draft.ToBlueprint());
    }

    [Fact]
    public void ManualMoveAndResize_KeepZoneIdentityAndUpdateTheSingleDesign()
    {
        var session = new DesktopWorkspaceDesignSession();
        Assert.True(session.TryApply(0, [new UpsertWorkspaceZone(Zone())]).Applied);
        var result = session.TryApply(1, [new UpsertWorkspaceZone(Zone("display-b", new(0.25, 0.25, 0.5, 0.5)))]);
        Assert.True(result.Applied);
        var zone = Assert.Single(session.Snapshot().State.Draft.Zones);
        Assert.Equal("work", zone.ZoneKey);
        Assert.Equal("display-b", zone.DisplayRef);
        Assert.Equal(new DesktopWorkspaceRelativeRect(0.25, 0.25, 0.5, 0.5), zone.Bounds);
    }

    [Fact]
    public void LateAiReply_CannotOverwriteManualEdit()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new UpsertWorkspaceZone(Zone())]);
        var pendingRequestRevision = session.Snapshot().Revision;
        session.TryApply(pendingRequestRevision, [new UpsertWorkspaceZone(Zone("display-b"))]);
        var result = session.TryApply(pendingRequestRevision, [new UpsertWorkspaceZone(Zone("display-c"))]);
        Assert.Equal(DesktopWorkspaceEditStatus.Stale, result.Status);
        Assert.Equal("display-b", Assert.Single(session.Snapshot().State.Draft.Zones).DisplayRef);
    }

    [Fact]
    public void InvalidCommandBatch_IsAtomicAndDoesNotCreateUndoEntry()
    {
        var session = new DesktopWorkspaceDesignSession();
        var result = session.TryApply(0, [new SetWorkspaceSummary("must not commit"), new UpsertWorkspaceZone(null!)]);
        Assert.Equal(DesktopWorkspaceEditStatus.Invalid, result.Status);
        Assert.Equal(0, session.Snapshot().Revision);
        Assert.Empty(session.Snapshot().State.Draft.Summary);
        Assert.Empty(session.Snapshot().UndoHistory);
    }

    [Fact]
    public void UndoCreatesNewRevision_AndDoesNotReviveAnOldModelResponse()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new SetWorkspaceSummary("A")]);
        session.TryApply(1, [new SetWorkspaceSummary("B")]);
        Assert.True(session.Undo(2).Applied);
        Assert.Equal("A", session.Snapshot().State.Draft.Summary);
        Assert.Equal(3, session.Snapshot().Revision);
        Assert.Equal(DesktopWorkspaceEditStatus.Stale, session.TryApply(1, [new SetWorkspaceSummary("old")]).Status);
        Assert.True(session.Redo(3).Applied);
        Assert.Equal("B", session.Snapshot().State.Draft.Summary);
    }

    [Fact]
    public void NewEditAfterUndo_DiscardsRedoBranch()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new SetWorkspaceSummary("A")]);
        session.Undo(1);
        session.TryApply(2, [new SetWorkspaceSummary("C")]);
        Assert.Equal(DesktopWorkspaceEditStatus.NothingToUndo, session.Redo(3).Status);
    }

    [Fact]
    public void RemovingZone_PreservesUnresolvedUserConstraintsUntilExplicitlyFixed()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new SetWorkspaceSummary("工作布局"), new UpsertWorkspaceZone(Zone()),
            new AssignWorkspaceCategory("documents", "work")]);
        session.TryApply(1, [new RemoveWorkspaceZone("work")]);
        Assert.Single(session.Snapshot().State.Draft.CategoryRules);
        Assert.Throws<InvalidOperationException>(() => session.Snapshot().State.Draft.ToBlueprint());
    }

    [Fact]
    public void CrossDisplayMove_DoesNotSilentlyEraseSpatialRelationships()
    {
        var draft = new DesktopWorkspaceDesignDraft(Guid.NewGuid(), "design", zones: [Zone(),
            new("other", "other", "display-a", new(0.5, 0, 0.5, 1))],
            zoneRelations: [new("work", DesktopWorkspaceZoneRelationKind.LeftOf, "other")]);
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new ReplaceWorkspaceDraft(draft)]);
        session.TryApply(1, [new UpsertWorkspaceZone(Zone("display-b"))]);
        Assert.Single(session.Snapshot().State.Draft.ZoneRelations);
        Assert.Throws<ArgumentException>(() => session.Snapshot().State.Draft.ToBlueprint());
    }

    [Fact]
    public void TranscriptUpdatesPersistenceSequence_WithoutInvalidatingAnUnchangedDesign()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.AppendMessage(DesktopWorkspaceMessageRole.User, "hello");
        Assert.Equal(0, session.Snapshot().Revision);
        Assert.Equal(1, session.Snapshot().Sequence);
        Assert.True(session.TryApply(0, [new SetWorkspaceSummary("design")]).Applied);
    }

    [Fact]
    public void UndoAlsoRestoresAccumulatedConversationIntent()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new ReplaceWorkspaceIntent(new("keep main screen clear"))]);
        session.Undo(1);
        Assert.Empty(session.Snapshot().State.Intent.Summary);
    }

    [Fact]
    public void HistoryAndTranscriptAreBounded()
    {
        var session = new DesktopWorkspaceDesignSession();
        for (var i = 0; i < 240; i++)
        {
            session.TryApply(i, [new SetWorkspaceSummary($"design {i}")]);
            session.AppendMessage(DesktopWorkspaceMessageRole.User, $"request {i}");
        }
        Assert.Equal(DesktopWorkspaceDesignSession.HistoryLimit, session.Snapshot().UndoHistory.Count);
        Assert.Equal(DesktopWorkspaceDesignSession.MessageLimit, session.Snapshot().Messages.Count);
    }

    [Fact]
    public async Task ConcurrentEditsWithSameBaseRevision_OnlyOneCanCommit()
    {
        var session = new DesktopWorkspaceDesignSession();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            session.TryApply(0, [new SetWorkspaceSummary($"choice {index}")]))));
        Assert.Single(results, result => result.Applied);
        Assert.Equal(19, results.Count(result => result.Status == DesktopWorkspaceEditStatus.Stale));
    }

    [Fact]
    public void SnapshotsDoNotChangeWhenFutureEditsCommit()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new UpsertWorkspaceZone(Zone())]);
        var first = session.Snapshot();
        session.TryApply(1, [new RemoveWorkspaceZone("work")]);
        Assert.Single(first.State.Draft.Zones);
        Assert.Empty(session.Snapshot().State.Draft.Zones);
    }

    [Fact]
    public void EmptyBatchDoesNotInvalidateCardsOrHistory()
    {
        var session = new DesktopWorkspaceDesignSession();
        Assert.Equal(DesktopWorkspaceEditStatus.NoChange, session.TryApply(0, []).Status);
        Assert.Equal(0, session.Snapshot().Sequence);
    }

    [Fact]
    public void RestoreRetainsUndoHistoryWithMonotonicRevision()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new SetWorkspaceSummary("A")]);
        var restored = DesktopWorkspaceDesignSession.Restore(session.Snapshot());
        restored.Undo(1);
        Assert.Equal(2, restored.Snapshot().Revision);
        Assert.Empty(restored.Snapshot().State.Draft.Summary);
    }

    private static DesktopWorkspaceBlueprintZone Zone(string display = "display-a", DesktopWorkspaceRelativeRect? bounds = null)
        => new("work", "工作", display, bounds ?? new(0, 0, 0.5, 1));
}
