using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// M4-06 日常分类工作流输入。只包含语义分类所需事实与当前模板 Zone，
/// 不包含物理坐标、WriterCommand 或任何桌面写入授权。
/// </summary>
public sealed class DesktopClassificationWorkflowRequest
{
    private readonly IReadOnlyList<IntelligenceZoneOption> _zones;

    public DesktopItemFacts Item { get; }
    public Guid ActiveTemplateId { get; }
    public string? LocalFilePath { get; }
    public IReadOnlyList<IntelligenceZoneOption> Zones => _zones;
    public bool ContentAnalysisEnabled { get; }
    public DesktopLayoutIconRule? MatchingLayoutRule { get; }

    public DesktopClassificationWorkflowRequest(
        DesktopItemFacts item,
        Guid activeTemplateId,
        IEnumerable<IntelligenceZoneOption>? zones = null,
        string? localFilePath = null,
        bool contentAnalysisEnabled = false,
        DesktopLayoutIconRule? matchingLayoutRule = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        if (activeTemplateId == Guid.Empty)
            throw new ArgumentException("ActiveTemplateId 不能为 Guid.Empty。", nameof(activeTemplateId));

        var copiedZones = (zones ?? Array.Empty<IntelligenceZoneOption>())
            .Take(DesktopClassificationPolicyResolver.MaxZonesPerResolution + 1)
            .ToArray();
        if (copiedZones.Length > DesktopClassificationPolicyResolver.MaxZonesPerResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zones),
                $"单次分类最多支持 {DesktopClassificationPolicyResolver.MaxZonesPerResolution} 个 Zone。");
        }
        if (copiedZones.Any(static zone => zone is null))
            throw new ArgumentException("Zones 不能包含 null。", nameof(zones));
        if (copiedZones.Select(static zone => zone.ZoneId).Distinct().Count() != copiedZones.Length)
            throw new ArgumentException("Zones 不能包含重复 ZoneId。", nameof(zones));

        ActiveTemplateId = activeTemplateId;
        LocalFilePath = localFilePath;
        ContentAnalysisEnabled = contentAnalysisEnabled;
        MatchingLayoutRule = matchingLayoutRule;
        _zones = Array.AsReadOnly(copiedZones);
    }
}

/// <summary>
/// M4 分类工作流结果。FinalPolicy 始终是本地 Policy Resolver 的产物；
/// Provider 失败时不会伪造 Suggestion，也不会覆盖已有本地决策。
/// </summary>
public sealed class DesktopClassificationWorkflowResult
{
    public DesktopClassificationPolicyResult LocalPolicy { get; }
    public DesktopClassificationPolicyResult FinalPolicy { get; }
    public bool ProviderAttempted { get; }
    public IntelligenceProviderResult? ProviderResult { get; }
    public DesktopTextExtractionResult? ContentAnalysis { get; }

    internal DesktopClassificationWorkflowResult(
        DesktopClassificationPolicyResult localPolicy,
        DesktopClassificationPolicyResult finalPolicy,
        bool providerAttempted,
        IntelligenceProviderResult? providerResult,
        DesktopTextExtractionResult? contentAnalysis)
    {
        LocalPolicy = localPolicy ?? throw new ArgumentNullException(nameof(localPolicy));
        FinalPolicy = finalPolicy ?? throw new ArgumentNullException(nameof(finalPolicy));

        if (providerAttempted && providerResult is null)
            throw new ArgumentException("已尝试 Provider 时必须提供 ProviderResult。", nameof(providerResult));
        if (!providerAttempted && (providerResult is not null || contentAnalysis is not null))
            throw new ArgumentException("未尝试 Provider 时不能携带 Provider / ContentAnalysis 结果。", nameof(providerAttempted));

        ProviderAttempted = providerAttempted;
        ProviderResult = providerResult;
        ContentAnalysis = contentAnalysis;
    }
}

/// <summary>
/// M4-06 UI-agnostic 分类编排：本地 Rules/Profile 优先，只有语义仍不足时才调用 AI。
/// 该类型不接触 Shell、M2 Planner、Coordinator、Recovery 或 Writer。
/// </summary>
public sealed class DesktopClassificationWorkflow
{
    private readonly IDesktopClassificationRuleStore _ruleStore;
    private readonly IDesktopUserProfileStore _profileStore;
    private readonly DesktopContentAwareClassificationRequestBuilder _requestBuilder;
    private readonly IDesktopIntelligenceProvider _provider;

    public DesktopClassificationWorkflow(
        IDesktopClassificationRuleStore ruleStore,
        IDesktopUserProfileStore profileStore,
        DesktopContentAwareClassificationRequestBuilder requestBuilder,
        IDesktopIntelligenceProvider provider)
    {
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _requestBuilder = requestBuilder ?? throw new ArgumentNullException(nameof(requestBuilder));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<DesktopClassificationWorkflowResult> ClassifyAsync(
        DesktopClassificationWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var rules = await _ruleStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var zoneIds = request.Zones.Select(static zone => zone.ZoneId).ToArray();

        var localWithoutProfile = DesktopClassificationPolicyResolver.Resolve(
            request.Item,
            request.ActiveTemplateId,
            rules,
            zoneIds,
            userProfilePreference: null,
            aiSuggestion: null,
            request.MatchingLayoutRule);

        var localPreference = await GetPreferenceForDecisionAsync(
                request.ActiveTemplateId,
                localWithoutProfile.Decision,
                cancellationToken)
            .ConfigureAwait(false);

        var localPolicy = localPreference is null
            ? localWithoutProfile
            : DesktopClassificationPolicyResolver.Resolve(
                request.Item,
                request.ActiveTemplateId,
                rules,
                zoneIds,
                localPreference,
                aiSuggestion: null,
                request.MatchingLayoutRule);

        if (!NeedsAi(localPolicy, request.Zones.Count))
        {
            return new DesktopClassificationWorkflowResult(
                localPolicy,
                localPolicy,
                providerAttempted: false,
                providerResult: null,
                contentAnalysis: null);
        }

        var build = await _requestBuilder.BuildAsync(
                request.Item,
                request.LocalFilePath,
                request.Zones,
                request.ContentAnalysisEnabled,
                cancellationToken)
            .ConfigureAwait(false);

        var providerResult = await _provider
            .ClassifyAsync(build.Request, cancellationToken)
            .ConfigureAwait(false);

        if (providerResult.Status != IntelligenceProviderStatus.Success)
        {
            return new DesktopClassificationWorkflowResult(
                localPolicy,
                localPolicy,
                providerAttempted: true,
                providerResult,
                build.ContentAnalysis);
        }

        // Preference 不参与 Category 决策，因此先用 AI Suggestion 确定最终 Category，
        // 再按最终 Category 查询一次 confirmed preference，避免漏掉 AI 新识别出的 Category。
        var withAiWithoutProfile = DesktopClassificationPolicyResolver.Resolve(
            request.Item,
            request.ActiveTemplateId,
            rules,
            zoneIds,
            userProfilePreference: null,
            providerResult.Suggestion,
            request.MatchingLayoutRule);

        var finalPreference = await GetPreferenceForDecisionAsync(
                request.ActiveTemplateId,
                withAiWithoutProfile.Decision,
                cancellationToken)
            .ConfigureAwait(false);

        var finalPolicy = finalPreference is null
            ? withAiWithoutProfile
            : DesktopClassificationPolicyResolver.Resolve(
                request.Item,
                request.ActiveTemplateId,
                rules,
                zoneIds,
                finalPreference,
                providerResult.Suggestion,
                request.MatchingLayoutRule);

        return new DesktopClassificationWorkflowResult(
            localPolicy,
            finalPolicy,
            providerAttempted: true,
            providerResult,
            build.ContentAnalysis);
    }

    private async Task<DesktopCategoryZonePreference?> GetPreferenceForDecisionAsync(
        Guid activeTemplateId,
        DesktopItemClassificationDecision? decision,
        CancellationToken cancellationToken)
    {
        if (decision is null)
            return null;

        return await _profileStore
            .GetPreferenceAsync(activeTemplateId, decision.Category, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool NeedsAi(DesktopClassificationPolicyResult localPolicy, int zoneCount)
    {
        // 同层冲突属于用户确认问题。更低 authority 的 AI 不能充当仲裁者。
        if (localPolicy.HasConflict)
            return false;

        if (localPolicy.Decision is null)
            return true;

        // 当前模板有 Zone，但本地知识只解决了 Category 时，可以让 AI 补充 Zone intent。
        return zoneCount > 0 && localPolicy.Decision.TargetZoneId is null;
    }
}
