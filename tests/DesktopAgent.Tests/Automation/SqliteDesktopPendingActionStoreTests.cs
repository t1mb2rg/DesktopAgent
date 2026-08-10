using DesktopAgent.Core.Automation;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Automation;

namespace DesktopAgent.Tests.Automation;

public sealed class SqliteDesktopPendingActionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopPendingActionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.Pending.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "pending.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // 尽力清理。
        }
    }

    [Fact]
    public async Task SaveGetAndDelete_RoundTripsQueuedDecision()
    {
        var store = new SqliteDesktopPendingActionStore(_dbPath);
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var action = CreateAction(id, created, templateId, zoneId);

        await store.SaveAsync(action);
        var loaded = await store.GetAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.PendingActionId);
        Assert.Equal(created, loaded.CreatedAtUtc);
        Assert.Equal(templateId, loaded.TemplateId);
        Assert.Equal("desktop:demo", loaded.Item.StableId);
        Assert.Equal("demo.pdf", loaded.Item.DisplayName);
        Assert.Equal(".pdf", loaded.Item.FileExtension);
        Assert.Equal(ItemKind.File, loaded.Item.ItemKind);
        Assert.Equal("Course", loaded.Decision!.Category);
        Assert.Equal(zoneId, loaded.Decision.TargetZoneId);
        Assert.Equal(0.82, loaded.Decision.Confidence);
        Assert.Equal(DesktopItemClassificationSource.AiProvider, loaded.Decision.Source);
        Assert.Equal(DesktopAutomationAuthorizationBasis.AiOnly, loaded.AuthorizationBasis);
        Assert.Equal(DesktopAutomationPolicyReason.AiOnlyUnconfirmed, loaded.QueueReason);

        Assert.True(await store.DeleteAsync(id));
        Assert.Null(await store.GetAsync(id));
        Assert.False(await store.DeleteAsync(id));
    }

    [Fact]
    public async Task GetPending_IsOldestFirstBoundedAndSaveIsUpsert()
    {
        var store = new SqliteDesktopPendingActionStore(_dbPath);
        await store.InitializeAsync();

        var baseTime = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var newer = CreateAction(Guid.NewGuid(), baseTime.AddMinutes(2));
        var oldest = CreateAction(Guid.NewGuid(), baseTime);
        var middle = CreateAction(Guid.NewGuid(), baseTime.AddMinutes(1));

        await store.SaveAsync(newer);
        await store.SaveAsync(oldest);
        await store.SaveAsync(middle);

        var firstTwo = await store.GetPendingAsync(2);
        Assert.Equal(new[] { oldest.PendingActionId, middle.PendingActionId },
            firstTwo.Select(action => action.PendingActionId));

        var replacement = new DesktopPendingAction(
            newer.PendingActionId,
            newer.CreatedAtUtc,
            null,
            newer.Item,
            new DesktopItemClassificationDecision(
                "Archive",
                null,
                0.9,
                DesktopItemClassificationSource.AiProvider,
                "replacement"),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed);
        await store.SaveAsync(replacement);

        Assert.Equal("Archive", (await store.GetAsync(newer.PendingActionId))!.Decision!.Category);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetPendingAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.GetPendingAsync(SqliteDesktopPendingActionStore.MaxPendingActions + 1));
    }

    [Fact]
    public async Task NoExecutableDecision_RoundTripsWithoutSyntheticDecision()
    {
        var store = new SqliteDesktopPendingActionStore(_dbPath);
        await store.InitializeAsync();

        var action = new DesktopPendingAction(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 10, 3, 0, 0, TimeSpan.Zero),
            null,
            new DesktopItemFacts("desktop:unknown", "unknown", ItemKind.Unknown, null, false, false),
            decision: null,
            DesktopAutomationAuthorizationBasis.None,
            DesktopAutomationPolicyReason.NoExecutableDecision);

        await store.SaveAsync(action);
        var loaded = Assert.Single(await store.GetPendingAsync(10));

        Assert.Null(loaded.Decision);
        Assert.Equal(DesktopAutomationPolicyReason.NoExecutableDecision, loaded.QueueReason);
    }

    private static DesktopPendingAction CreateAction(
        Guid id,
        DateTimeOffset createdAtUtc,
        Guid? templateId = null,
        Guid? zoneId = null)
        => new(
            id,
            createdAtUtc,
            templateId,
            new DesktopItemFacts("desktop:demo", "demo.pdf", ItemKind.File, ".pdf", false, true),
            new DesktopItemClassificationDecision(
                "Course",
                zoneId,
                0.82,
                DesktopItemClassificationSource.AiProvider,
                "AI suggestion"),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed);
}
