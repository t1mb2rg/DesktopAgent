using DesktopAgent.Core.Automation;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopAutomation;

/// <summary>
/// M5-02 Pending Queue + Confirm & Remember 应用层协调器。
///
/// 该协调器只处理语义授权与长期记忆，不生成 WriterCommand，也不执行物理桌面动作。
/// Confirm 后返回的 DirectUserRequest authority 由后续 fresh execution pipeline 再做安全校验。
/// </summary>
public sealed class DesktopPendingActionCoordinator
{
    private readonly IDesktopPendingActionStore _pendingStore;
    private readonly IDesktopClassificationRuleStore _ruleStore;
    private readonly IDesktopUserProfileStore _profileStore;
    private readonly TimeProvider _timeProvider;

    public DesktopPendingActionCoordinator(
        IDesktopPendingActionStore pendingStore,
        IDesktopClassificationRuleStore ruleStore,
        IDesktopUserProfileStore profileStore,
        TimeProvider? timeProvider = null)
    {
        _pendingStore = pendingStore ?? throw new ArgumentNullException(nameof(pendingStore));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _pendingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _ruleStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _profileStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task QueueAsync(
        DesktopPendingAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _pendingStore.SaveAsync(action, cancellationToken);
    }

    public Task<IReadOnlyList<DesktopPendingAction>> GetPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
        => _pendingStore.GetPendingAsync(limit, cancellationToken);

    public async Task<DesktopPendingActionResolution> ConfirmOnceAsync(
        Guid pendingActionId,
        CancellationToken cancellationToken = default)
    {
        var action = await GetRequiredActionAsync(pendingActionId, cancellationToken).ConfigureAwait(false);
        EnsureExecutableDecision(action);

        await DeleteRequiredAsync(action.PendingActionId, cancellationToken).ConfigureAwait(false);

        return new DesktopPendingActionResolution(
            action,
            DesktopPendingActionResolutionKind.ConfirmOnce,
            DesktopAutomationAuthorizationBasis.DirectUserRequest);
    }

    public async Task<DesktopPendingActionResolution> ConfirmAndRememberAsync(
        Guid pendingActionId,
        DesktopPendingActionRememberDirective remember,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remember);
        remember.Validate();

        var action = await GetRequiredActionAsync(pendingActionId, cancellationToken).ConfigureAwait(false);
        var decision = EnsureExecutableDecision(action);

        Guid? rememberedRuleId = null;
        Guid? rememberedPreferenceId = null;

        switch (remember.Scope)
        {
            case DesktopPendingActionRememberScope.ExactItem:
            {
                var rule = BuildRememberedRule(
                    action,
                    decision,
                    new DesktopClassificationRuleCondition(stableId: action.Item.StableId),
                    $"Remember item: {action.Item.DisplayName}");
                await _ruleStore.SaveAsync(rule, cancellationToken).ConfigureAwait(false);
                rememberedRuleId = rule.RuleId;
                break;
            }

            case DesktopPendingActionRememberScope.FileExtension:
            {
                if (action.Item.FileExtension is null)
                    throw new InvalidOperationException("该 Pending Action 没有可记忆的 FileExtension。");

                var rule = BuildRememberedRule(
                    action,
                    decision,
                    new DesktopClassificationRuleCondition(fileExtension: action.Item.FileExtension),
                    $"Remember extension: {action.Item.FileExtension}");
                await _ruleStore.SaveAsync(rule, cancellationToken).ConfigureAwait(false);
                rememberedRuleId = rule.RuleId;
                break;
            }

            case DesktopPendingActionRememberScope.CategoryZonePreference:
            {
                if (action.TemplateId is not Guid templateId || decision.TargetZoneId is not Guid zoneId)
                {
                    throw new InvalidOperationException(
                        "CategoryZonePreference 需要 Pending Action 同时包含 TemplateId 与 TargetZoneId。");
                }

                var existing = await _profileStore
                    .GetPreferenceAsync(templateId, decision.Category, cancellationToken)
                    .ConfigureAwait(false);
                var evidenceCount = existing is null
                    ? 1
                    : existing.EvidenceCount == int.MaxValue
                        ? int.MaxValue
                        : existing.EvidenceCount + 1;

                var preference = new DesktopCategoryZonePreference(
                    action.PendingActionId,
                    templateId,
                    decision.Category,
                    zoneId,
                    confidence: 1d,
                    evidenceCount,
                    _timeProvider.GetUtcNow());

                await _profileStore.SavePreferenceAsync(preference, cancellationToken).ConfigureAwait(false);
                rememberedPreferenceId = preference.PreferenceId;
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(remember));
        }

        // Memory 使用 PendingActionId 作为稳定主键，因此“写记忆成功、删除 Queue 前崩溃”后重试不会制造重复规则。
        await DeleteRequiredAsync(action.PendingActionId, cancellationToken).ConfigureAwait(false);

        return new DesktopPendingActionResolution(
            action,
            DesktopPendingActionResolutionKind.ConfirmAndRemember,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            rememberedRuleId,
            rememberedPreferenceId);
    }

    public async Task<DesktopPendingActionResolution> RejectAsync(
        Guid pendingActionId,
        CancellationToken cancellationToken = default)
    {
        var action = await GetRequiredActionAsync(pendingActionId, cancellationToken).ConfigureAwait(false);
        await DeleteRequiredAsync(action.PendingActionId, cancellationToken).ConfigureAwait(false);

        return new DesktopPendingActionResolution(
            action,
            DesktopPendingActionResolutionKind.Reject,
            DesktopAutomationAuthorizationBasis.None);
    }

    private async Task<DesktopPendingAction> GetRequiredActionAsync(
        Guid pendingActionId,
        CancellationToken cancellationToken)
    {
        if (pendingActionId == Guid.Empty)
            throw new ArgumentException("PendingActionId 不能为 Guid.Empty。", nameof(pendingActionId));

        return await _pendingStore.GetAsync(pendingActionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pending Action \"{pendingActionId:D}\" 不存在。");
    }

    private static DesktopItemClassificationDecision EnsureExecutableDecision(DesktopPendingAction action)
        => action.Decision
           ?? throw new InvalidOperationException(
               "该 Pending Action 尚无 executable classification decision，不能 Confirm；应先由产品流程补全决定或 Reject。");

    private async Task DeleteRequiredAsync(Guid pendingActionId, CancellationToken cancellationToken)
    {
        if (!await _pendingStore.DeleteAsync(pendingActionId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Pending Action \"{pendingActionId:D}\" 在完成处理前已被移除。");
        }
    }

    private static DesktopClassificationRule BuildRememberedRule(
        DesktopPendingAction action,
        DesktopItemClassificationDecision decision,
        DesktopClassificationRuleCondition condition,
        string ruleName)
    {
        var provenance = decision.Source == DesktopItemClassificationSource.AiProvider
            ? DesktopClassificationRuleProvenance.AiProposedAndConfirmed
            : DesktopClassificationRuleProvenance.LearnedAndConfirmed;

        return new DesktopClassificationRule(
            action.PendingActionId,
            TruncateRuleName(ruleName),
            condition,
            decision.Category,
            action.TemplateId.HasValue && decision.TargetZoneId.HasValue ? action.TemplateId : null,
            action.TemplateId.HasValue && decision.TargetZoneId.HasValue ? decision.TargetZoneId : null,
            DesktopClassificationRuleAuthority.ExplicitUser,
            provenance,
            priority: 0,
            isEnabled: true);
    }

    private static string TruncateRuleName(string value)
    {
        const int maxLength = DesktopClassificationRuleDraft.MaxNameLength;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
