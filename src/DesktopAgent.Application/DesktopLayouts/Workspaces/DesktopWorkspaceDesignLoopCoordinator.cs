using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

public enum DesktopWorkspaceDesignLoopStatus
{
    Success = 0,
    ProviderFailed = 1,
    CompileBlocked = 2,
    DesignUnresolved = 3,
    EquivalenceFailed = 4,
}

/// <summary>
/// 一次有界 Workspace Design loop 的终态。
/// Success 才同时携带 Blueprint + Exact Plan + Realization Certificate；
/// 其他状态只返回本地诊断，不授予 Preview / Candidate / Writer authority。
/// </summary>
public sealed class DesktopWorkspaceDesignLoopResult
{
    private readonly IReadOnlyList<DesktopWorkspaceCompileIssue> _compileIssues;
    private readonly IReadOnlyList<DesktopWorkspaceEquivalenceIssue> _equivalenceIssues;

    public DesktopWorkspaceDesignLoopStatus Status { get; }
    public int DesignAttempts { get; }
    public DesktopWorkspaceBlueprint? Blueprint { get; }
    public CompiledWorkspacePlan? Plan { get; }
    public WorkspaceRealizationCertificate? Certificate { get; }
    public IntelligenceProviderStatus? ProviderStatus { get; }
    public string? DiagnosticMessage { get; }
    public IReadOnlyList<DesktopWorkspaceCompileIssue> CompileIssues => _compileIssues;
    public IReadOnlyList<DesktopWorkspaceEquivalenceIssue> EquivalenceIssues => _equivalenceIssues;
    public bool Succeeded => Status == DesktopWorkspaceDesignLoopStatus.Success &&
                             Blueprint is not null &&
                             Plan is not null &&
                             Certificate is not null;

    private DesktopWorkspaceDesignLoopResult(
        DesktopWorkspaceDesignLoopStatus status,
        int designAttempts,
        DesktopWorkspaceBlueprint? blueprint = null,
        CompiledWorkspacePlan? plan = null,
        WorkspaceRealizationCertificate? certificate = null,
        IntelligenceProviderStatus? providerStatus = null,
        string? diagnosticMessage = null,
        IEnumerable<DesktopWorkspaceCompileIssue>? compileIssues = null,
        IEnumerable<DesktopWorkspaceEquivalenceIssue>? equivalenceIssues = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (designAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(designAttempts));

        var compileArray = compileIssues?.ToArray() ?? [];
        var equivalenceArray = equivalenceIssues?.ToArray() ?? [];
        if (compileArray.Any(static issue => issue is null))
            throw new ArgumentException("CompileIssues 不能包含 null。", nameof(compileIssues));
        if (equivalenceArray.Any(static issue => issue is null))
            throw new ArgumentException("EquivalenceIssues 不能包含 null。", nameof(equivalenceIssues));

        if (status == DesktopWorkspaceDesignLoopStatus.Success)
        {
            if (blueprint is null || plan is null || certificate is null ||
                compileArray.Length > 0 || equivalenceArray.Length > 0 || providerStatus is not null)
            {
                throw new ArgumentException("Success 必须完整携带 Blueprint / Plan / Certificate 且不能携带失败状态。 ");
            }
        }
        else if (plan is not null || certificate is not null)
        {
            throw new ArgumentException("非 Success 状态不能携带可执行 Exact Plan 或 Certificate。 ");
        }

        Status = status;
        DesignAttempts = designAttempts;
        Blueprint = blueprint;
        Plan = plan;
        Certificate = certificate;
        ProviderStatus = providerStatus;
        DiagnosticMessage = diagnosticMessage;
        _compileIssues = Array.AsReadOnly(compileArray);
        _equivalenceIssues = Array.AsReadOnly(equivalenceArray);
    }

    internal static DesktopWorkspaceDesignLoopResult Success(
        int attempts,
        DesktopWorkspaceBlueprint blueprint,
        CompiledWorkspacePlan plan,
        WorkspaceRealizationCertificate certificate)
        => new(
            DesktopWorkspaceDesignLoopStatus.Success,
            attempts,
            blueprint ?? throw new ArgumentNullException(nameof(blueprint)),
            plan ?? throw new ArgumentNullException(nameof(plan)),
            certificate ?? throw new ArgumentNullException(nameof(certificate)));

    internal static DesktopWorkspaceDesignLoopResult ProviderFailed(
        int attempts,
        IntelligenceProviderStatus providerStatus,
        string? diagnosticMessage)
        => new(
            DesktopWorkspaceDesignLoopStatus.ProviderFailed,
            attempts,
            providerStatus: providerStatus,
            diagnosticMessage: diagnosticMessage);

    internal static DesktopWorkspaceDesignLoopResult CompileBlocked(
        int attempts,
        DesktopWorkspaceBlueprint blueprint,
        IEnumerable<DesktopWorkspaceCompileIssue> issues)
        => new(
            DesktopWorkspaceDesignLoopStatus.CompileBlocked,
            attempts,
            blueprint: blueprint ?? throw new ArgumentNullException(nameof(blueprint)),
            compileIssues: issues);

    internal static DesktopWorkspaceDesignLoopResult DesignUnresolved(
        int attempts,
        DesktopWorkspaceBlueprint blueprint,
        IEnumerable<DesktopWorkspaceCompileIssue> issues)
        => new(
            DesktopWorkspaceDesignLoopStatus.DesignUnresolved,
            attempts,
            blueprint: blueprint ?? throw new ArgumentNullException(nameof(blueprint)),
            compileIssues: issues);

    internal static DesktopWorkspaceDesignLoopResult EquivalenceFailed(
        int attempts,
        DesktopWorkspaceBlueprint blueprint,
        IEnumerable<DesktopWorkspaceEquivalenceIssue> issues)
        => new(
            DesktopWorkspaceDesignLoopStatus.EquivalenceFailed,
            attempts,
            blueprint: blueprint ?? throw new ArgumentNullException(nameof(blueprint)),
            equivalenceIssues: issues);
}

/// <summary>
/// ADR-0005 Phase 6：有界 AI redesign loop。
///
/// AI 只在“设计可修复”的 compile failure 上获得再次设计机会；
/// Compiler raw issue 永远留在本地，Provider 只得到经过白名单映射的 realization diagnostics。
/// 每次 redesign 使用同一个 BlueprintId，并由本地严格递增 immutable revision。
/// </summary>
public sealed class DesktopWorkspaceDesignLoopCoordinator
{
    public const int DefaultMaxDesignAttempts = 3;
    public const int AbsoluteMaxDesignAttempts = 5;

    private readonly IDesktopWorkspaceDesigner _designer;
    private readonly int _maxDesignAttempts;

    public DesktopWorkspaceDesignLoopCoordinator(
        IDesktopWorkspaceDesigner designer,
        int maxDesignAttempts = DefaultMaxDesignAttempts)
    {
        _designer = designer ?? throw new ArgumentNullException(nameof(designer));
        if (maxDesignAttempts is <= 0 or > AbsoluteMaxDesignAttempts)
            throw new ArgumentOutOfRangeException(nameof(maxDesignAttempts));
        _maxDesignAttempts = maxDesignAttempts;
    }

    public async Task<DesktopWorkspaceDesignLoopResult> DesignAndCompileAsync(
        DesktopWorkspaceDesignRequest initialRequest,
        DesktopSemanticUnderstanding semanticUnderstanding,
        ExplorerWorkspaceGeometry geometry,
        IEnumerable<DesktopWorkspaceCompilationItemFact> itemFacts,
        IEnumerable<DesktopWorkspaceCompilationItemRefBinding>? itemRefBindings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialRequest);
        ArgumentNullException.ThrowIfNull(semanticUnderstanding);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(itemFacts);

        var factArray = itemFacts.ToArray();
        var bindingArray = itemRefBindings?.ToArray() ?? [];
        if (factArray.Any(static fact => fact is null))
            throw new ArgumentException("ItemFacts 不能包含 null。", nameof(itemFacts));
        if (bindingArray.Any(static binding => binding is null))
            throw new ArgumentException("ItemRefBindings 不能包含 null。", nameof(itemRefBindings));

        var request = initialRequest;
        for (var attempt = 1; attempt <= _maxDesignAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var providerResult = await _designer
                .DesignAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!providerResult.Succeeded)
            {
                return DesktopWorkspaceDesignLoopResult.ProviderFailed(
                    attempt,
                    providerResult.Status,
                    providerResult.DiagnosticMessage);
            }

            var blueprint = providerResult.Blueprint!;
            var compileResult = DesktopWorkspaceCompiler.Compile(
                blueprint,
                semanticUnderstanding,
                geometry,
                factArray,
                bindingArray);

            if (compileResult.Succeeded)
            {
                var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
                    blueprint,
                    geometry,
                    compileResult.Plan!,
                    factArray,
                    bindingArray);
                if (!equivalence.Equivalent)
                {
                    return DesktopWorkspaceDesignLoopResult.EquivalenceFailed(
                        attempt,
                        blueprint,
                        equivalence.Issues);
                }

                return DesktopWorkspaceDesignLoopResult.Success(
                    attempt,
                    blueprint,
                    compileResult.Plan!,
                    equivalence.Certificate!);
            }

            if (!AllIssuesAreDesignFixable(compileResult.Issues))
            {
                return DesktopWorkspaceDesignLoopResult.CompileBlocked(
                    attempt,
                    blueprint,
                    compileResult.Issues);
            }

            if (attempt == _maxDesignAttempts)
            {
                return DesktopWorkspaceDesignLoopResult.DesignUnresolved(
                    attempt,
                    blueprint,
                    compileResult.Issues);
            }

            var diagnostics = BuildProviderSafeDiagnostics(
                compileResult.Issues,
                semanticUnderstanding,
                blueprint,
                bindingArray);
            request = CreateRevisionRequest(request, blueprint, diagnostics);
        }

        throw new InvalidOperationException("Workspace Design loop 不应越过有界 attempt 终点。 ");
    }

    private static bool AllIssuesAreDesignFixable(IReadOnlyList<DesktopWorkspaceCompileIssue> issues)
        => issues.Count > 0 && issues.All(static issue => issue.Code is
            DesktopWorkspaceCompileIssueCode.PreserveDisplayConflict or
            DesktopWorkspaceCompileIssueCode.NonMovableItemRequiresExplicitPolicy or
            DesktopWorkspaceCompileIssueCode.MissingCategoryRule or
            DesktopWorkspaceCompileIssueCode.ZoneQuantizesToEmpty or
            DesktopWorkspaceCompileIssueCode.ReservedRegionQuantizesToEmpty or
            DesktopWorkspaceCompileIssueCode.ZoneRelationViolated or
            DesktopWorkspaceCompileIssueCode.ZoneOverlap or
            DesktopWorkspaceCompileIssueCode.ZoneCapacityInsufficient);

    private static IReadOnlyList<DesktopWorkspaceDesignRealizationDiagnostic> BuildProviderSafeDiagnostics(
        IReadOnlyList<DesktopWorkspaceCompileIssue> issues,
        DesktopSemanticUnderstanding semanticUnderstanding,
        DesktopWorkspaceBlueprint previousBlueprint,
        IReadOnlyList<DesktopWorkspaceCompilationItemRefBinding> bindings)
    {
        var itemRefByStableId = bindings.ToDictionary(
            static binding => binding.StableId,
            static binding => binding.ItemRef,
            StringComparer.Ordinal);
        var categoryByStableId = semanticUnderstanding.Assignments.ToDictionary(
            static assignment => assignment.StableId,
            static assignment => assignment.CategoryKey,
            StringComparer.Ordinal);

        var diagnostics = new List<DesktopWorkspaceDesignRealizationDiagnostic>();
        foreach (var issue in issues)
        {
            var code = MapDiagnosticCode(issue.Code);
            var itemRef = issue.StableId is not null && itemRefByStableId.TryGetValue(issue.StableId, out var safeItemRef)
                ? safeItemRef
                : null;
            var categoryKey = issue.StableId is not null && categoryByStableId.TryGetValue(issue.StableId, out var safeCategoryKey)
                ? safeCategoryKey
                : null;
            var elementKey = issue.Code == DesktopWorkspaceCompileIssueCode.ReservedRegionQuantizesToEmpty
                ? ResolveSingleReservedRegionKey(previousBlueprint, issue.DisplayRef)
                : issue.ZoneKey;

            diagnostics.Add(new(
                code,
                SafeDetail(code),
                issue.DisplayRef,
                elementKey,
                categoryKey,
                itemRef,
                issue.RequiredSlots,
                issue.AvailableSlots));
        }

        var bounded = diagnostics
            .GroupBy(static diagnostic => new
            {
                diagnostic.Code,
                diagnostic.DisplayRef,
                diagnostic.ElementKey,
                diagnostic.CategoryKey,
                diagnostic.ItemRef,
                diagnostic.RequiredSlots,
                diagnostic.AvailableSlots,
            })
            .Select(static group => group.First())
            .OrderBy(static diagnostic => diagnostic.Code)
            .ThenBy(static diagnostic => diagnostic.DisplayRef, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.ElementKey, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.CategoryKey, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.ItemRef, StringComparer.Ordinal)
            .Take(DesktopWorkspaceDesignRequest.MaxRealizationDiagnostics)
            .ToArray();
        return Array.AsReadOnly(bounded);
    }

    private static DesktopWorkspaceDesignRealizationDiagnosticCode MapDiagnosticCode(
        DesktopWorkspaceCompileIssueCode code)
        => code switch
        {
            DesktopWorkspaceCompileIssueCode.PreserveDisplayConflict =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.PreserveDisplayConflict,
            DesktopWorkspaceCompileIssueCode.NonMovableItemRequiresExplicitPolicy =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.NonMovableItemRequiresExplicitPolicy,
            DesktopWorkspaceCompileIssueCode.MissingCategoryRule =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.MissingCategoryRule,
            DesktopWorkspaceCompileIssueCode.ZoneQuantizesToEmpty =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneQuantizesToEmpty,
            DesktopWorkspaceCompileIssueCode.ReservedRegionQuantizesToEmpty =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.ReservedRegionQuantizesToEmpty,
            DesktopWorkspaceCompileIssueCode.ZoneRelationViolated =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneRelationViolated,
            DesktopWorkspaceCompileIssueCode.ZoneOverlap =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneOverlap,
            DesktopWorkspaceCompileIssueCode.ZoneCapacityInsufficient =>
                DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneCapacityInsufficient,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "该 compile issue 不允许反馈给 AI Designer。"),
        };

    private static string SafeDetail(DesktopWorkspaceDesignRealizationDiagnosticCode code)
        => code switch
        {
            DesktopWorkspaceDesignRealizationDiagnosticCode.PreserveDisplayConflict =>
                "上一版设计要求与 Preserve 显示器的不变策略冲突。",
            DesktopWorkspaceDesignRealizationDiagnosticCode.NonMovableItemRequiresExplicitPolicy =>
                "Design 显示器上存在不可移动项目，Blueprint 必须明确处理该设计约束。",
            DesktopWorkspaceDesignRealizationDiagnosticCode.MissingCategoryRule =>
                "上一版 Blueprint 没有为当前语义分类提供完整的 Category→Zone 设计规则。",
            DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneQuantizesToEmpty =>
                "该 Zone 在当前原生槽位粒度下无法忠实量化成非空区域。",
            DesktopWorkspaceDesignRealizationDiagnosticCode.ReservedRegionQuantizesToEmpty =>
                "该 Reserved Region 在当前原生槽位粒度下无法忠实量化成非空区域。",
            DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneRelationViolated =>
                "上一版 Zone 空间关系在最近原生量化后无法同时保持。",
            DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneOverlap =>
                "上一版 Zone 在最近原生量化后发生重叠，Compiler 不允许自行裁切。",
            DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneCapacityInsufficient =>
                "该 Zone 忠实量化后的可用容量不足，Compiler 不允许自行扩大或溢出到其他 Zone。",
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };

    private static string? ResolveSingleReservedRegionKey(
        DesktopWorkspaceBlueprint blueprint,
        string? displayRef)
    {
        if (displayRef is null)
            return null;

        var matches = blueprint.ReservedRegions
            .Where(region => string.Equals(region.DisplayRef, displayRef, StringComparison.Ordinal))
            .Select(static region => region.RegionKey)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static DesktopWorkspaceDesignRequest CreateRevisionRequest(
        DesktopWorkspaceDesignRequest previousRequest,
        DesktopWorkspaceBlueprint previousBlueprint,
        IReadOnlyList<DesktopWorkspaceDesignRealizationDiagnostic> diagnostics)
        => new(
            new DesktopWorkspaceDesignRevisionTarget(
                previousBlueprint.BlueprintId,
                checked(previousBlueprint.Revision + 1),
                previousBlueprint.Revision),
            previousRequest.DesktopSummary,
            previousRequest.UserGuidance,
            previousRequest.Displays,
            previousRequest.Categories,
            previousRequest.CriticalItems,
            previousBlueprint,
            diagnostics);
}
