using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopClassificationWorkflowAcceptanceTests
{
    [Fact]
    public async Task LocalRuleAndConfirmedPreference_AreEnough_SkipsAiAndContentIo()
    {
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var preferenceId = Guid.NewGuid();
        var rules = new InMemoryRuleStore(CreateUserRule(
            category: "Course",
            extension: ".pdf"));
        var profiles = new InMemoryProfileStore(new DesktopCategoryZonePreference(
            preferenceId,
            templateId,
            "Course",
            zoneId,
            confidence: 0.95,
            evidenceCount: 4,
            DateTimeOffset.UtcNow));
        var extractor = new ThrowIfCalledExtractor();
        var provider = new ThrowIfCalledProvider();
        var workflow = CreateWorkflow(rules, profiles, extractor, provider);

        var result = await workflow.ClassifyAsync(new DesktopClassificationWorkflowRequest(
            CreateFacts(),
            templateId,
            new[] { new IntelligenceZoneOption(zoneId, "课程") },
            localFilePath: @"C:\private\report.pdf",
            contentAnalysisEnabled: true));

        Assert.False(result.ProviderAttempted);
        Assert.Null(result.ProviderResult);
        Assert.Null(result.ContentAnalysis);
        Assert.Equal(0, extractor.CallCount);
        Assert.Equal(0, provider.CallCount);

        var decision = Assert.IsType<DesktopItemClassificationDecision>(result.FinalPolicy.Decision);
        Assert.Equal("Course", decision.Category);
        Assert.Equal(zoneId, decision.TargetZoneId);
        Assert.Equal(DesktopItemClassificationSource.UserRule, decision.Source);
        Assert.Equal(preferenceId, result.FinalPolicy.TargetZonePreferenceId);
        Assert.False(result.FinalPolicy.RequiresUserConfirmation);
    }

    [Fact]
    public async Task AiSuccess_RequeriesConfirmedPreferenceForAiCategory_AndUsesBoundedContent()
    {
        var templateId = Guid.NewGuid();
        var aiZoneId = Guid.NewGuid();
        var preferredZoneId = Guid.NewGuid();
        var preferenceId = Guid.NewGuid();
        var profiles = new InMemoryProfileStore(new DesktopCategoryZonePreference(
            preferenceId,
            templateId,
            "Course",
            preferredZoneId,
            confidence: 0.9,
            evidenceCount: 3,
            DateTimeOffset.UtcNow));
        var excerpt = new DesktopItemContentExcerpt(
            ".pdf",
            "课程实验报告的受控摘要",
            wasTruncated: true);
        var extractor = new FixedExtractor(excerpt);
        var provider = new CapturingProvider(IntelligenceProviderResult.Success(
            new DesktopItemClassificationSuggestion(
                "Course",
                aiZoneId,
                0.84,
                "正文与文件名都指向课程资料",
                DesktopItemClassificationSource.AiProvider)));
        var workflow = CreateWorkflow(new InMemoryRuleStore(), profiles, extractor, provider);

        var result = await workflow.ClassifyAsync(new DesktopClassificationWorkflowRequest(
            CreateFacts(),
            templateId,
            new[]
            {
                new IntelligenceZoneOption(aiZoneId, "AI 候选区"),
                new IntelligenceZoneOption(preferredZoneId, "已确认课程区"),
            },
            localFilePath: @"C:\private\report.pdf",
            contentAnalysisEnabled: true));

        Assert.True(result.ProviderAttempted);
        Assert.Equal(IntelligenceProviderStatus.Success, result.ProviderResult!.Status);
        Assert.Equal(1, extractor.CallCount);
        Assert.Equal(1, provider.CallCount);
        Assert.Same(excerpt, provider.LastRequest!.ContentExcerpt);
        Assert.True(provider.LastRequest.ContentAnalysisEnabled);

        var decision = Assert.IsType<DesktopItemClassificationDecision>(result.FinalPolicy.Decision);
        Assert.Equal("Course", decision.Category);
        Assert.Equal(preferredZoneId, decision.TargetZoneId);
        Assert.Equal(DesktopItemClassificationSource.AiProvider, decision.Source);
        Assert.Equal(preferenceId, result.FinalPolicy.TargetZonePreferenceId);
        Assert.True(result.FinalPolicy.RequiresUserConfirmation);
    }

    [Fact]
    public async Task ExplicitUserCategory_BeatsLowerAiCategory_WhileAiMayFillMissingZone()
    {
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var rule = CreateUserRule(category: "Course", extension: ".pdf");
        var provider = new CapturingProvider(IntelligenceProviderResult.Success(
            new DesktopItemClassificationSuggestion(
                "Games",
                zoneId,
                0.99,
                "AI 猜测为 Games",
                DesktopItemClassificationSource.AiProvider)));
        var workflow = CreateWorkflow(
            new InMemoryRuleStore(rule),
            new InMemoryProfileStore(),
            new ThrowIfCalledExtractor(),
            provider);

        var result = await workflow.ClassifyAsync(new DesktopClassificationWorkflowRequest(
            CreateFacts(),
            templateId,
            new[] { new IntelligenceZoneOption(zoneId, "候选区") },
            contentAnalysisEnabled: false));

        Assert.True(result.ProviderAttempted);
        var decision = Assert.IsType<DesktopItemClassificationDecision>(result.FinalPolicy.Decision);
        Assert.Equal("Course", decision.Category);
        Assert.Equal(DesktopItemClassificationSource.UserRule, decision.Source);
        Assert.Equal(zoneId, decision.TargetZoneId);
        Assert.Equal(rule.RuleId, result.FinalPolicy.CategoryRuleId);
        Assert.True(result.FinalPolicy.RequiresUserConfirmation);
    }

    [Theory]
    [InlineData(IntelligenceProviderStatus.NotConfigured)]
    [InlineData(IntelligenceProviderStatus.Unavailable)]
    [InlineData(IntelligenceProviderStatus.Timeout)]
    [InlineData(IntelligenceProviderStatus.Cancelled)]
    [InlineData(IntelligenceProviderStatus.InvalidResponse)]
    public async Task AiFailureStates_PreserveExistingLocalDecisionAndNeverInventZone(
        IntelligenceProviderStatus failureStatus)
    {
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var rule = CreateUserRule(category: "Course", extension: ".pdf");
        var provider = new CapturingProvider(IntelligenceProviderResult.Failure(
            failureStatus,
            "acceptance failure"));
        var workflow = CreateWorkflow(
            new InMemoryRuleStore(rule),
            new InMemoryProfileStore(),
            new ThrowIfCalledExtractor(),
            provider);

        var result = await workflow.ClassifyAsync(new DesktopClassificationWorkflowRequest(
            CreateFacts(),
            templateId,
            new[] { new IntelligenceZoneOption(zoneId, "课程") },
            contentAnalysisEnabled: false));

        Assert.True(result.ProviderAttempted);
        Assert.Equal(failureStatus, result.ProviderResult!.Status);
        Assert.Same(result.LocalPolicy, result.FinalPolicy);

        var decision = Assert.IsType<DesktopItemClassificationDecision>(result.FinalPolicy.Decision);
        Assert.Equal("Course", decision.Category);
        Assert.Equal(DesktopItemClassificationSource.UserRule, decision.Source);
        Assert.Null(decision.TargetZoneId);
    }

    [Fact]
    public async Task ContentAnalysisDisabled_AiMayRunButExtractorIsNeverCalledAndExcerptStaysNull()
    {
        var templateId = Guid.NewGuid();
        var extractor = new ThrowIfCalledExtractor();
        var provider = new CapturingProvider(IntelligenceProviderResult.Success(
            new DesktopItemClassificationSuggestion(
                "Document",
                suggestedZoneId: null,
                confidence: 0.7,
                reason: "metadata only",
                DesktopItemClassificationSource.AiProvider)));
        var workflow = CreateWorkflow(
            new InMemoryRuleStore(),
            new InMemoryProfileStore(),
            extractor,
            provider);

        var result = await workflow.ClassifyAsync(new DesktopClassificationWorkflowRequest(
            CreateFacts(),
            templateId,
            zones: null,
            localFilePath: @"C:\private\report.pdf",
            contentAnalysisEnabled: false));

        Assert.True(result.ProviderAttempted);
        Assert.Equal(0, extractor.CallCount);
        Assert.Equal(1, provider.CallCount);
        Assert.False(provider.LastRequest!.ContentAnalysisEnabled);
        Assert.Null(provider.LastRequest.ContentExcerpt);
        Assert.Equal(DesktopTextExtractionStatus.Disabled, result.ContentAnalysis!.Status);
    }

    [Fact]
    public async Task LocalConflict_StopsBeforeAiAndContentBecauseAiCannotArbitrateUserRules()
    {
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var rules = new InMemoryRuleStore(
            CreateUserRule("Course", ".pdf"),
            CreateUserRule("Development", ".pdf"));
        var extractor = new ThrowIfCalledExtractor();
        var provider = new ThrowIfCalledProvider();
        var workflow = CreateWorkflow(rules, new InMemoryProfileStore(), extractor, provider);

        var result = await workflow.ClassifyAsync(new DesktopClassificationWorkflowRequest(
            CreateFacts(),
            templateId,
            new[] { new IntelligenceZoneOption(zoneId, "任意区") },
            localFilePath: @"C:\private\report.pdf",
            contentAnalysisEnabled: true));

        Assert.True(result.FinalPolicy.HasConflict);
        Assert.True(result.FinalPolicy.RequiresUserConfirmation);
        Assert.False(result.ProviderAttempted);
        Assert.Equal(0, extractor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task NoLocalKnowledgeAndAiFailure_EndsWithoutDecisionInsteadOfFabricatingFallback()
    {
        var workflow = CreateWorkflow(
            new InMemoryRuleStore(),
            new InMemoryProfileStore(),
            new ThrowIfCalledExtractor(),
            new CapturingProvider(IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                "malformed structured output")));

        var result = await workflow.ClassifyAsync(new DesktopClassificationWorkflowRequest(
            CreateFacts(),
            Guid.NewGuid(),
            contentAnalysisEnabled: false));

        Assert.True(result.ProviderAttempted);
        Assert.Null(result.LocalPolicy.Decision);
        Assert.Null(result.FinalPolicy.Decision);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.ProviderResult!.Status);
    }

    [Fact]
    public void ApplicationAssembly_RemainsIndependentFromPersistenceAndWindowsInterop()
    {
        var references = typeof(DesktopClassificationWorkflow)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("DesktopAgent.Persistence", references);
        Assert.DoesNotContain("DesktopAgent.WindowsInterop", references);
        Assert.DoesNotContain("DesktopAgent.Poc", references);
    }

    private static DesktopClassificationWorkflow CreateWorkflow(
        IDesktopClassificationRuleStore rules,
        IDesktopUserProfileStore profiles,
        IDesktopTextContentExtractor extractor,
        IDesktopIntelligenceProvider provider)
        => new(
            rules,
            profiles,
            new DesktopContentAwareClassificationRequestBuilder(extractor),
            provider);

    private static DesktopClassificationRule CreateUserRule(string category, string extension)
        => new(
            Guid.NewGuid(),
            $"{category} rule",
            new DesktopClassificationRuleCondition(fileExtension: extension),
            category,
            targetTemplateId: null,
            targetZoneId: null,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);

    private static DesktopItemFacts CreateFacts()
        => new(
            "private-stable-id",
            "实验报告.pdf",
            ItemKind.File,
            ".pdf",
            isSystemItem: false,
            isFileSystemItem: true);

    private sealed class InMemoryRuleStore : IDesktopClassificationRuleStore
    {
        private readonly List<DesktopClassificationRule> _rules;

        public InMemoryRuleStore(params DesktopClassificationRule[] rules)
            => _rules = rules.ToList();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveAsync(DesktopClassificationRule rule, CancellationToken cancellationToken = default)
        {
            var index = _rules.FindIndex(existing => existing.RuleId == rule.RuleId);
            if (index >= 0)
                _rules[index] = rule;
            else
                _rules.Add(rule);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DesktopClassificationRule>> GetAllAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopClassificationRule>>(_rules.ToArray());

        public Task<bool> DeleteAsync(Guid ruleId, CancellationToken cancellationToken = default)
            => Task.FromResult(_rules.RemoveAll(rule => rule.RuleId == ruleId) > 0);
    }

    private sealed class InMemoryProfileStore : IDesktopUserProfileStore
    {
        private readonly List<DesktopCategoryZonePreference> _preferences;

        public InMemoryProfileStore(params DesktopCategoryZonePreference[] preferences)
            => _preferences = preferences.ToList();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SavePreferenceAsync(
            DesktopCategoryZonePreference preference,
            CancellationToken cancellationToken = default)
        {
            _preferences.RemoveAll(existing =>
                existing.TemplateId == preference.TemplateId &&
                string.Equals(existing.Category, preference.Category, StringComparison.OrdinalIgnoreCase));
            _preferences.Add(preference);
            return Task.CompletedTask;
        }

        public Task<DesktopCategoryZonePreference?> GetPreferenceAsync(
            Guid templateId,
            string category,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_preferences.FirstOrDefault(preference =>
                preference.TemplateId == templateId &&
                string.Equals(preference.Category, category, StringComparison.OrdinalIgnoreCase)));

        public Task<bool> DeletePreferenceAsync(Guid preferenceId, CancellationToken cancellationToken = default)
            => Task.FromResult(_preferences.RemoveAll(preference => preference.PreferenceId == preferenceId) > 0);

        public Task AppendEvidenceAsync(
            DesktopClassificationBehaviorEvidence evidence,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DesktopClassificationBehaviorEvidence>> GetRecentEvidenceAsync(
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopClassificationBehaviorEvidence>>(
                Array.Empty<DesktopClassificationBehaviorEvidence>());

        public Task<int> ClearEvidenceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class CapturingProvider : IDesktopIntelligenceProvider
    {
        private readonly IntelligenceProviderResult _result;

        public int CallCount { get; private set; }
        public DesktopItemClassificationRequest? LastRequest { get; private set; }

        public CapturingProvider(IntelligenceProviderResult result) => _result = result;

        public Task<IntelligenceProviderResult> ClassifyAsync(
            DesktopItemClassificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowIfCalledProvider : IDesktopIntelligenceProvider
    {
        public int CallCount { get; private set; }

        public Task<IntelligenceProviderResult> ClassifyAsync(
            DesktopItemClassificationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("本地知识足够时不应调用 AI Provider。");
        }
    }

    private sealed class FixedExtractor : IDesktopTextContentExtractor
    {
        private readonly DesktopItemContentExcerpt _excerpt;

        public int CallCount { get; private set; }

        public FixedExtractor(DesktopItemContentExcerpt excerpt) => _excerpt = excerpt;

        public Task<DesktopTextExtractionResult> ExtractAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(DesktopTextExtractionResult.Success(_excerpt));
        }
    }

    private sealed class ThrowIfCalledExtractor : IDesktopTextContentExtractor
    {
        public int CallCount { get; private set; }

        public Task<DesktopTextExtractionResult> ExtractAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("此验收路径不应读取正文。");
        }
    }
}
