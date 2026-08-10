using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Core.Intelligence;

public enum DesktopClassificationPolicyField
{
    Category = 0,
    TargetZone = 1,
}

/// <summary>
/// 同一决策层级出现互相矛盾结果时的可审计冲突。
/// Resolver 仍使用稳定 tie-break 得到可重复结果，但会要求用户确认。
/// </summary>
public sealed class DesktopClassificationPolicyConflict
{
    private readonly IReadOnlyList<string> _candidateKeys;
    private readonly IReadOnlyList<string> _proposedValues;

    public DesktopClassificationPolicyField Field { get; }
    public IReadOnlyList<string> CandidateKeys => _candidateKeys;
    public IReadOnlyList<string> ProposedValues => _proposedValues;

    public DesktopClassificationPolicyConflict(
        DesktopClassificationPolicyField field,
        IEnumerable<string> candidateKeys,
        IEnumerable<string> proposedValues)
    {
        if (!Enum.IsDefined(field))
            throw new ArgumentOutOfRangeException(nameof(field));
        ArgumentNullException.ThrowIfNull(candidateKeys);
        ArgumentNullException.ThrowIfNull(proposedValues);

        var copiedKeys = candidateKeys.ToArray();
        var copiedValues = proposedValues.ToArray();
        if (copiedKeys.Length < 2 || copiedKeys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Conflict 至少需要两个有效 CandidateKey。", nameof(candidateKeys));
        if (copiedValues.Length < 2 || copiedValues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Conflict 至少需要两个有效 ProposedValue。", nameof(proposedValues));

        Field = field;
        _candidateKeys = Array.AsReadOnly(copiedKeys);
        _proposedValues = Array.AsReadOnly(copiedValues);
    }
}

/// <summary>一次分类 Policy 解析的完整、UI 无关结果。</summary>
public sealed class DesktopClassificationPolicyResult
{
    private readonly IReadOnlyList<Guid> _matchedRuleIds;
    private readonly IReadOnlyList<DesktopClassificationPolicyConflict> _conflicts;

    public DesktopItemClassificationDecision? Decision { get; }
    public Guid? CategoryRuleId { get; }
    public Guid? TargetZoneRuleId { get; }
    public bool TargetZoneOverriddenByLayoutRule { get; }
    public IReadOnlyList<Guid> MatchedRuleIds => _matchedRuleIds;
    public IReadOnlyList<DesktopClassificationPolicyConflict> Conflicts => _conflicts;
    public bool HasConflict => _conflicts.Count > 0;

    internal DesktopClassificationPolicyResult(
        DesktopItemClassificationDecision? decision,
        Guid? categoryRuleId,
        Guid? targetZoneRuleId,
        bool targetZoneOverriddenByLayoutRule,
        IEnumerable<Guid> matchedRuleIds,
        IEnumerable<DesktopClassificationPolicyConflict> conflicts)
    {
        Decision = decision;
        CategoryRuleId = categoryRuleId;
        TargetZoneRuleId = targetZoneRuleId;
        TargetZoneOverriddenByLayoutRule = targetZoneOverriddenByLayoutRule;
        _matchedRuleIds = Array.AsReadOnly(matchedRuleIds.ToArray());
        _conflicts = Array.AsReadOnly(conflicts.ToArray());
    }
}

/// <summary>
/// M4 分类 Policy：分别解析 Category 与 TargetZone，再组合为 ClassificationDecision。
/// M2 StableId ZoneOverride 只覆盖 TargetZone；Exclude / Pin 仍完全属于下游布局执行语义。
/// </summary>
public static class DesktopClassificationPolicyResolver
{
    public const int MaxRulesPerResolution = 4096;

    public static DesktopClassificationPolicyResult Resolve(
        DesktopItemFacts item,
        IEnumerable<DesktopClassificationRule> rules,
        IEnumerable<Guid> validZoneIds,
        DesktopItemClassificationSuggestion? aiSuggestion = null,
        DesktopLayoutIconRule? matchingLayoutRule = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(validZoneIds);

        var copiedRules = rules.Take(MaxRulesPerResolution + 1).ToArray();
        if (copiedRules.Length > MaxRulesPerResolution)
            throw new ArgumentOutOfRangeException(nameof(rules), $"单次 Policy 解析最多支持 {MaxRulesPerResolution} 条规则。");
        if (copiedRules.Any(static rule => rule is null))
            throw new ArgumentException("Rules 不能包含 null。", nameof(rules));
        if (copiedRules.Select(static rule => rule.RuleId).Distinct().Count() != copiedRules.Length)
            throw new ArgumentException("Rules 不能包含重复 RuleId。", nameof(rules));

        var zoneIds = new HashSet<Guid>();
        foreach (var zoneId in validZoneIds)
        {
            if (zoneId == Guid.Empty)
                throw new ArgumentException("validZoneIds 不能包含 Guid.Empty。", nameof(validZoneIds));
            zoneIds.Add(zoneId);
        }

        foreach (var rule in copiedRules)
        {
            if (rule.IsEnabled && rule.TargetZoneId.HasValue && !zoneIds.Contains(rule.TargetZoneId.Value))
            {
                throw new ArgumentException(
                    $"启用规则 {rule.RuleId} 指向当前布局不存在的 TargetZoneId {rule.TargetZoneId}。",
                    nameof(rules));
            }
        }

        if (matchingLayoutRule is not null)
        {
            if (!string.Equals(matchingLayoutRule.StableId, item.StableId, StringComparison.Ordinal))
                throw new ArgumentException("matchingLayoutRule 必须与当前 Item StableId 精确匹配。", nameof(matchingLayoutRule));
            if (matchingLayoutRule.Kind == DesktopLayoutIconRuleKind.ZoneOverride &&
                !zoneIds.Contains(matchingLayoutRule.TargetZoneId!.Value))
            {
                throw new ArgumentException(
                    "matchingLayoutRule 的 ZoneOverride 指向当前布局不存在的 Zone。",
                    nameof(matchingLayoutRule));
            }
        }

        if (aiSuggestion?.SuggestedZoneId is Guid aiZoneId && !zoneIds.Contains(aiZoneId))
            throw new ArgumentException("AI Suggestion 指向当前布局不存在的 Zone。", nameof(aiSuggestion));

        var candidates = new List<PolicyCandidate>();
        var matchedRuleIds = new List<Guid>();

        foreach (var rule in copiedRules)
        {
            if (!rule.IsEnabled || !rule.Condition.Matches(item))
                continue;

            matchedRuleIds.Add(rule.RuleId);
            candidates.Add(PolicyCandidate.FromRule(rule));
        }

        if (aiSuggestion is not null)
            candidates.Add(PolicyCandidate.FromAi(aiSuggestion));

        PolicyCandidate? layoutZoneOverride = null;
        if (matchingLayoutRule?.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
            layoutZoneOverride = PolicyCandidate.FromLayoutZoneOverride(matchingLayoutRule);

        var conflicts = new List<DesktopClassificationPolicyConflict>();
        var categoryWinner = SelectCategoryWinner(candidates, conflicts);
        var zoneWinner = SelectZoneWinner(candidates, layoutZoneOverride, conflicts);

        matchedRuleIds.Sort();

        if (categoryWinner is null)
        {
            return new DesktopClassificationPolicyResult(
                null,
                null,
                zoneWinner?.RuleId,
                zoneWinner?.IsLayoutZoneOverride == true,
                matchedRuleIds,
                conflicts);
        }

        var requiresUserConfirmation =
            categoryWinner.Source == DesktopItemClassificationSource.AiProvider ||
            zoneWinner?.Source == DesktopItemClassificationSource.AiProvider ||
            conflicts.Count > 0;

        var explanation = BuildExplanation(categoryWinner, zoneWinner, conflicts.Count > 0);
        var decision = new DesktopItemClassificationDecision(
            item.StableId,
            categoryWinner.Category!,
            zoneWinner?.TargetZoneId,
            categoryWinner.Confidence,
            categoryWinner.Source,
            explanation,
            requiresUserConfirmation);

        return new DesktopClassificationPolicyResult(
            decision,
            categoryWinner.RuleId,
            zoneWinner?.RuleId,
            zoneWinner?.IsLayoutZoneOverride == true,
            matchedRuleIds,
            conflicts);
    }

    private static PolicyCandidate? SelectCategoryWinner(
        IReadOnlyList<PolicyCandidate> candidates,
        ICollection<DesktopClassificationPolicyConflict> conflicts)
    {
        var categoryCandidates = candidates
            .Where(static candidate => candidate.Category is not null)
            .OrderBy(static candidate => candidate, PolicyCandidateComparer.Instance)
            .ToArray();
        if (categoryCandidates.Length == 0)
            return null;

        var winner = categoryCandidates[0];
        var peers = categoryCandidates
            .TakeWhile(candidate => candidate.HasSameDecisionRank(winner))
            .ToArray();

        var distinctCategories = peers
            .Select(static candidate => candidate.Category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctCategories.Length > 1)
        {
            conflicts.Add(new DesktopClassificationPolicyConflict(
                DesktopClassificationPolicyField.Category,
                peers.Select(static candidate => candidate.CandidateKey),
                distinctCategories));
        }

        return winner;
    }

    private static PolicyCandidate? SelectZoneWinner(
        IReadOnlyList<PolicyCandidate> candidates,
        PolicyCandidate? layoutZoneOverride,
        ICollection<DesktopClassificationPolicyConflict> conflicts)
    {
        if (layoutZoneOverride is not null)
            return layoutZoneOverride;

        var zoneCandidates = candidates
            .Where(static candidate => candidate.TargetZoneId.HasValue)
            .OrderBy(static candidate => candidate, PolicyCandidateComparer.Instance)
            .ToArray();
        if (zoneCandidates.Length == 0)
            return null;

        var winner = zoneCandidates[0];
        var peers = zoneCandidates
            .TakeWhile(candidate => candidate.HasSameDecisionRank(winner))
            .ToArray();

        var distinctZones = peers
            .Select(static candidate => candidate.TargetZoneId!.Value)
            .Distinct()
            .ToArray();
        if (distinctZones.Length > 1)
        {
            conflicts.Add(new DesktopClassificationPolicyConflict(
                DesktopClassificationPolicyField.TargetZone,
                peers.Select(static candidate => candidate.CandidateKey),
                distinctZones.Select(static zoneId => zoneId.ToString("D"))));
        }

        return winner;
    }

    private static string BuildExplanation(
        PolicyCandidate categoryWinner,
        PolicyCandidate? zoneWinner,
        bool hasConflict)
    {
        var categoryText = $"Category={categoryWinner.Category} 由 {categoryWinner.DescribeSource()} 决定";
        var zoneText = zoneWinner is null
            ? "未形成 TargetZone 决策"
            : $"TargetZone={zoneWinner.TargetZoneId:D} 由 {zoneWinner.DescribeSource()} 决定";
        var conflictText = hasConflict
            ? "；存在同层冲突，已使用稳定 tie-break 并要求用户确认"
            : string.Empty;

        return $"{categoryText}；{zoneText}{conflictText}。";
    }

    private sealed class PolicyCandidate
    {
        public string CandidateKey { get; }
        public string? Category { get; }
        public Guid? TargetZoneId { get; }
        public double Confidence { get; }
        public DesktopItemClassificationSource Source { get; }
        public int AuthorityRank { get; }
        public int Priority { get; }
        public int Specificity { get; }
        public Guid? RuleId { get; }
        public string SourceDescription { get; }
        public bool IsLayoutZoneOverride { get; }

        private PolicyCandidate(
            string candidateKey,
            string? category,
            Guid? targetZoneId,
            double confidence,
            DesktopItemClassificationSource source,
            int authorityRank,
            int priority,
            int specificity,
            Guid? ruleId,
            string sourceDescription,
            bool isLayoutZoneOverride)
        {
            CandidateKey = candidateKey;
            Category = category;
            TargetZoneId = targetZoneId;
            Confidence = confidence;
            Source = source;
            AuthorityRank = authorityRank;
            Priority = priority;
            Specificity = specificity;
            RuleId = ruleId;
            SourceDescription = sourceDescription;
            IsLayoutZoneOverride = isLayoutZoneOverride;
        }

        public static PolicyCandidate FromRule(DesktopClassificationRule rule)
            => new(
                $"rule:{rule.RuleId:D}",
                rule.Category,
                rule.TargetZoneId,
                1d,
                rule.ClassificationSource,
                rule.Authority == DesktopClassificationRuleAuthority.ExplicitUser ? 0 : 1,
                rule.Priority,
                rule.Condition.SpecificityScore,
                rule.RuleId,
                $"规则“{rule.Name}”({rule.Provenance})",
                false);

        public static PolicyCandidate FromAi(DesktopItemClassificationSuggestion suggestion)
            => new(
                "ai:suggestion",
                suggestion.Category,
                suggestion.SuggestedZoneId,
                suggestion.Confidence,
                DesktopItemClassificationSource.AiProvider,
                2,
                0,
                0,
                null,
                "AI Suggestion",
                false);

        public static PolicyCandidate FromLayoutZoneOverride(DesktopLayoutIconRule rule)
            => new(
                "m2:zone-override",
                null,
                rule.TargetZoneId,
                1d,
                DesktopItemClassificationSource.ExplicitUserRule,
                -1,
                int.MaxValue,
                int.MaxValue,
                null,
                "M2 StableId ZoneOverride",
                true);

        public bool HasSameDecisionRank(PolicyCandidate other)
            => AuthorityRank == other.AuthorityRank &&
               Priority == other.Priority &&
               Specificity == other.Specificity;

        public string DescribeSource() => SourceDescription;
    }

    private sealed class PolicyCandidateComparer : IComparer<PolicyCandidate>
    {
        public static PolicyCandidateComparer Instance { get; } = new();

        public int Compare(PolicyCandidate? x, PolicyCandidate? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            var authority = x.AuthorityRank.CompareTo(y.AuthorityRank);
            if (authority != 0) return authority;

            var priority = y.Priority.CompareTo(x.Priority);
            if (priority != 0) return priority;

            var specificity = y.Specificity.CompareTo(x.Specificity);
            if (specificity != 0) return specificity;

            return StringComparer.Ordinal.Compare(x.CandidateKey, y.CandidateKey);
        }
    }
}
