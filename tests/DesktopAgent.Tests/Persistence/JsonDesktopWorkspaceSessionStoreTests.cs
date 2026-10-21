using System.Text.Json.Nodes;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces;

namespace DesktopAgent.Tests.Persistence;

public sealed class JsonDesktopWorkspaceSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TesselSessionTests", Guid.NewGuid().ToString("N"));
    private string SessionPath => Path.Combine(_directory, "session.json");

    [Fact]
    public async Task ComplexDraft_RetainsConstraintsAndReservedGeometryWithoutExecutionState()
    {
        var session = new DesktopWorkspaceDesignSession();
        var draft = new DesktopWorkspaceDesignDraft(Guid.NewGuid(), "full design",
            displayPolicies: [new("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones: [new("left", "左侧", "primary", new(0, 0, 0.4, 1), DesktopWorkspaceAnchorKind.Center),
                new("right", "右侧", "primary", new(0.6, 0, 0.4, 1))],
            zoneRelations: [new("left", DesktopWorkspaceZoneRelationKind.LeftOf, "right")],
            categoryRules: [new("documents", "left")],
            itemOverrides: [new("item_1", DesktopWorkspaceItemOverrideKind.Keep)],
            reservedRegions: [new("center", "壁纸", "primary", new(0.4, 0, 0.2, 1))]);
        session.TryApply(0, [new ReplaceWorkspaceDraft(draft), new ReplaceWorkspaceIntent(new("intent",
            displayLoads: new Dictionary<string, DesktopWorkspaceDisplayLoad> { ["primary"] = DesktopWorkspaceDisplayLoad.Low },
            reservedRegions: [new("wallpaper", "primary", DesktopAgent.Core.Intelligence.DesktopZonePlacementHint.Center,
                DesktopAgent.Core.Intelligence.DesktopZoneSizeHint.Medium, "壁纸")]))]);
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        await store.SaveAsync(session.Snapshot());
        var loaded = (await store.LoadAsync())!;
        Assert.Equal(DesktopWorkspaceAnchorKind.Center, loaded.State.Draft.Zones[0].Anchor);
        Assert.Equal("right", Assert.Single(loaded.State.Draft.ZoneRelations).OtherZoneKey);
        Assert.Equal(DesktopWorkspaceItemOverrideKind.Keep, Assert.Single(loaded.State.Draft.ItemOverrides).Kind);
        Assert.Equal(new DesktopWorkspaceRelativeRect(0.4, 0, 0.2, 1), Assert.Single(loaded.State.Draft.ReservedRegions).Bounds);
        Assert.Equal(DesktopWorkspaceDisplayLoad.Low, loaded.State.Intent.DisplayLoads["primary"]);
        Assert.Equal("wallpaper", Assert.Single(loaded.State.Intent.ReservedRegions).Key);
    }

    [Fact]
    public async Task MissingSession_ReturnsNullWithoutCreatingOne()
    {
        Assert.Null(await new JsonDesktopWorkspaceSessionStore(SessionPath).LoadAsync());
        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task RoundTrip_RetainsZoneGeometryIntentTranscriptAndUndo()
    {
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        var session = Session();
        await store.SaveAsync(session.Snapshot());
        var loaded = (await store.LoadAsync())!;
        var zone = Assert.Single(loaded.State.Draft.Zones);
        Assert.Equal(new DesktopWorkspaceRelativeRect(0.25, 0.125, 0.5, 0.75), zone.Bounds);
        Assert.Equal("display-b", zone.DisplayRef);
        Assert.Equal("display-b", loaded.State.Intent.CategoryDisplayTargets["documents"]);
        Assert.Equal("user request", Assert.Single(loaded.Messages).Text);
        Assert.True(DesktopWorkspaceDesignSession.Restore(loaded).Undo(loaded.Revision).Applied);
    }

    [Fact]
    public async Task IncompleteDesignWithDanglingRule_CanBeSavedWithoutInventingLayout()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new AssignWorkspaceCategory("documents", "future-zone")]);
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        await store.SaveAsync(session.Snapshot());
        var loaded = (await store.LoadAsync())!;
        Assert.Empty(loaded.State.Draft.Zones);
        Assert.Single(loaded.State.Draft.CategoryRules);
        Assert.Throws<InvalidOperationException>(() => loaded.State.Draft.ToBlueprint());
    }

    [Fact]
    public async Task OlderQueuedSave_CannotOverwriteNewerRevision()
    {
        var session = Session();
        var older = session.Snapshot();
        session.TryApply(older.Revision, [new SetWorkspaceSummary("latest")]);
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        await store.SaveAsync(session.Snapshot());
        await store.SaveAsync(older);
        Assert.Equal("latest", (await store.LoadAsync())!.State.Draft.Summary);
    }

    [Fact]
    public async Task SameSequenceWithConflictingContent_IsRejected()
    {
        var snapshot = Session().Snapshot();
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        await store.SaveAsync(snapshot);
        var other = snapshot with { State = snapshot.State with { Draft = snapshot.State.Draft.With(summary: "conflict") } };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(other));
        Assert.Equal(snapshot.State.Draft.Summary, (await store.LoadAsync())!.State.Draft.Summary);
    }

    [Fact]
    public async Task CorruptRecord_IsPreservedAndCannotBeSilentlyReplaced()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SessionPath, "broken");
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Session().Snapshot()));
        Assert.Equal("broken", await File.ReadAllTextAsync(SessionPath));
    }

    [Theory]
    [InlineData("applyTicket")]
    [InlineData("writerCommand")]
    [InlineData("frozenPreview")]
    public async Task UnknownExecutionFields_AreRejectedOnRestore(string field)
    {
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        await store.SaveAsync(Session().Snapshot());
        var root = JsonNode.Parse(await File.ReadAllTextAsync(SessionPath))!;
        root[field] = "not authority";
        await File.WriteAllTextAsync(SessionPath, root.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task RepeatedSaveOfSameSnapshot_IsIdempotent()
    {
        var store = new JsonDesktopWorkspaceSessionStore(SessionPath);
        var snapshot = Session().Snapshot();
        await store.SaveAsync(snapshot);
        await store.SaveAsync(snapshot);
        Assert.Equal(snapshot.Sequence, (await store.LoadAsync())!.Sequence);
    }

    private static DesktopWorkspaceDesignSession Session()
    {
        var session = new DesktopWorkspaceDesignSession();
        session.TryApply(0, [new SetWorkspaceSummary("design"),
            new UpsertWorkspaceZone(new("work", "工作", "display-b", new(0.25, 0.125, 0.5, 0.75))),
            new ReplaceWorkspaceIntent(new("intent", new Dictionary<string, string> { ["documents"] = "display-b" }))]);
        session.AppendMessage(DesktopWorkspaceMessageRole.User, "user request");
        return session;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
