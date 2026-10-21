using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Automation;

public sealed class DesktopPendingActionCoordinatorTests
{
    [Fact]
    public async Task ConfirmOnce_RemovesQueueAndGrantsOnlyThisExecution()
    {
        var pending = new PendingStore();
        var action = Action();
        await pending.SaveAsync(action);
        var coordinator = CreateCoordinator(pending);

        var result = await coordinator.ConfirmOnceAsync(action.PendingActionId);

        Assert.Equal(DesktopPendingActionResolutionKind.ConfirmOnce, result.Kind);
        Assert.Equal(DesktopAutomationAuthorizationBasis.DirectUserRequest, result.ExecutionAuthorizationBasis);
        Assert.Null(result.RememberedRuleId);
        Assert.Null(result.RememberedPreferenceId);
        Assert.Null(await pending.GetAsync(action.PendingActionId));
    }

    [Fact]
    public async Task ConfirmAndRemember_ExactItemCreatesIdempotentExplicitUserRule()
    {
        var pending = new PendingStore();
        var rules = new RuleStore();
        var action = Action(templateId: Guid.NewGuid(), zoneId: Guid.NewGuid());
        await pending.SaveAsync(action);
        var coordinator = CreateCoordinator(pending, rules);

        var result = await coordinator.ConfirmAndRememberAsync(
            action.PendingActionId,
            new DesktopPendingActionRememberDirective(DesktopPendingActionRememberScope.ExactItem));

        var rule = Assert.Single(rules.Rules);
        Assert.Equal(action.PendingActionId, rule.RuleId);
        Assert.Equal(action.Item.StableId, rule.Condition.StableId);
        Assert.Null(rule.Condition.FileExtension);
        Assert.Equal(DesktopClassificationRuleAuthority.ExplicitUser, rule.Authority);
        Assert.Equal(DesktopClassificationRuleProvenance.AiProposedAndConfirmed, rule.Provenance);
        Assert.Equal(action.TemplateId, rule.TargetTemplateId);
        Assert.Equal(action.Decision!.TargetZoneId, rule.TargetZoneId);
        Assert.Equal(action.PendingActionId, result.RememberedRuleId);
        Assert.Null(await pending.GetAsync(action.PendingActionId));
    }

    [Fact]
    public async Task ConfirmAndRemember_FileExtensionRequiresExplicitAvailableExtension()
    {
        var pending = new PendingStore();
        var rules = new RuleStore();
        var action = Action();
        await pending.SaveAsync(action);
        var coordinator = CreateCoordinator(pending, rules);

        await coordinator.ConfirmAndRememberAsync(
            action.PendingActionId,
            new DesktopPendingActionRememberDirective(DesktopPendingActionRememberScope.FileExtension));

        Assert.Equal(".pdf", Assert.Single(rules.Rules).Condition.FileExtension);

        var withoutExtension = Action(
            item: new DesktopItemFacts("desktop:noext", "README", ItemKind.File, null, false, true));
        await pending.SaveAsync(withoutExtension);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ConfirmAndRememberAsync(
                withoutExtension.PendingActionId,
                new DesktopPendingActionRememberDirective(DesktopPendingActionRememberScope.FileExtension)));

        Assert.NotNull(await pending.GetAsync(withoutExtension.PendingActionId));
    }

    [Fact]
    public async Task ConfirmAndRemember_CategoryZonePreferenceUsesConfirmedProfileMemory()
    {
        var pending = new PendingStore();
        var profile = new ProfileStore();
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var action = Action(templateId: templateId, zoneId: zoneId);
        await pending.SaveAsync(action);
        await profile.SavePreferenceAsync(new DesktopCategoryZonePreference(
            Guid.NewGuid(), templateId, "Course", Guid.NewGuid(), 0.5, 3,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));

        var now = new DateTimeOffset(2026, 8, 10, 12, 30, 0, TimeSpan.Zero);
        var coordinator = CreateCoordinator(pending, profile: profile, timeProvider: new FixedTimeProvider(now));

        var result = await coordinator.ConfirmAndRememberAsync(
            action.PendingActionId,
            new DesktopPendingActionRememberDirective(
                DesktopPendingActionRememberScope.CategoryZonePreference));

        var saved = await profile.GetPreferenceAsync(templateId, "Course");
        Assert.NotNull(saved);
        Assert.Equal(action.PendingActionId, saved.PreferenceId);
        Assert.Equal(zoneId, saved.PreferredZoneId);
        Assert.Equal(1d, saved.Confidence);
        Assert.Equal(4, saved.EvidenceCount);
        Assert.Equal(now, saved.ConfirmedAtUtc);
        Assert.Equal(action.PendingActionId, result.RememberedPreferenceId);
    }

    [Fact]
    public async Task MissingDecision_CanBeRejectedButCannotBeConfirmed()
    {
        var pending = new PendingStore();
        var action = new DesktopPendingAction(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            new DesktopItemFacts("desktop:unknown", "unknown", ItemKind.Unknown, null, false, false),
            null,
            DesktopAutomationAuthorizationBasis.None,
            DesktopAutomationPolicyReason.NoExecutableDecision);
        await pending.SaveAsync(action);
        var coordinator = CreateCoordinator(pending);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ConfirmOnceAsync(action.PendingActionId));
        Assert.NotNull(await pending.GetAsync(action.PendingActionId));

        var rejected = await coordinator.RejectAsync(action.PendingActionId);
        Assert.Equal(DesktopPendingActionResolutionKind.Reject, rejected.Kind);
        Assert.Equal(DesktopAutomationAuthorizationBasis.None, rejected.ExecutionAuthorizationBasis);
        Assert.Null(await pending.GetAsync(action.PendingActionId));
    }

    private static DesktopPendingActionCoordinator CreateCoordinator(
        PendingStore pending,
        RuleStore? rules = null,
        ProfileStore? profile = null,
        TimeProvider? timeProvider = null)
        => new(pending, rules ?? new RuleStore(), profile ?? new ProfileStore(), timeProvider);

    private static DesktopPendingAction Action(
        Guid? templateId = null,
        Guid? zoneId = null,
        DesktopItemFacts? item = null)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            templateId,
            item ?? new DesktopItemFacts("desktop:demo", "demo.pdf", ItemKind.File, ".pdf", false, true),
            new DesktopItemClassificationDecision(
                "Course", zoneId, 0.85, DesktopItemClassificationSource.AiProvider, "AI suggestion"),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed);

    private sealed class PendingStore : IDesktopPendingActionStore
    {
        private readonly Dictionary<Guid, DesktopPendingAction> _items = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(DesktopPendingAction action, CancellationToken cancellationToken = default)
        {
            _items[action.PendingActionId] = action;
            return Task.CompletedTask;
        }

        public Task<DesktopPendingAction?> GetAsync(Guid pendingActionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.GetValueOrDefault(pendingActionId));

        public Task<IReadOnlyList<DesktopPendingAction>> GetPendingAsync(
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopPendingAction>>(
                _items.Values.OrderBy(x => x.CreatedAtUtc).Take(limit).ToArray());

        public Task<bool> DeleteAsync(Guid pendingActionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.Remove(pendingActionId));
    }

    private sealed class RuleStore : IDesktopClassificationRuleStore
    {
        private readonly Dictionary<Guid, DesktopClassificationRule> _rules = new();
        public IReadOnlyCollection<DesktopClassificationRule> Rules => _rules.Values;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(DesktopClassificationRule rule, CancellationToken cancellationToken = default)
        {
            _rules[rule.RuleId] = rule;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DesktopClassificationRule>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopClassificationRule>>(_rules.Values.ToArray());

        public Task<bool> DeleteAsync(Guid ruleId, CancellationToken cancellationToken = default)
            => Task.FromResult(_rules.Remove(ruleId));
    }

    private sealed class ProfileStore : IDesktopUserProfileStore
    {
        private readonly Dictionary<(Guid TemplateId, string Category), DesktopCategoryZonePreference> _preferences = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SavePreferenceAsync(
            DesktopCategoryZonePreference preference,
            CancellationToken cancellationToken = default)
        {
            _preferences[(preference.TemplateId, preference.Category.ToUpperInvariant())] = preference;
            return Task.CompletedTask;
        }

        public Task<DesktopCategoryZonePreference?> GetPreferenceAsync(
            Guid templateId,
            string category,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                _preferences.GetValueOrDefault((templateId, category.ToUpperInvariant())));

        public Task<bool> DeletePreferenceAsync(Guid preferenceId, CancellationToken cancellationToken = default)
        {
            var key = _preferences.FirstOrDefault(pair => pair.Value.PreferenceId == preferenceId).Key;
            return Task.FromResult(!key.Equals(default((Guid, string))) && _preferences.Remove(key));
        }

        public Task AppendEvidenceAsync(
            DesktopClassificationBehaviorEvidence evidence,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DesktopClassificationBehaviorEvidence>> GetRecentEvidenceAsync(
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopClassificationBehaviorEvidence>>(Array.Empty<DesktopClassificationBehaviorEvidence>());

        public Task<int> ClearEvidenceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
