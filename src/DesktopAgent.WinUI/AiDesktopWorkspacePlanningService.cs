using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.Intelligence;

namespace DesktopAgent.WinUI;

internal enum AiDesktopWorkspacePlanningProductStatus
{
    Success = 0,
    AiDisabled = 1,
    GeometryUnavailable = 2,
    PreparationFailed = 3,
    ProviderFailed = 4,
    CompileBlocked = 5,
    DesignUnresolved = 6,
    EquivalenceFailed = 7,
    PreviewFailed = 8,
}

internal sealed record AiDesktopWorkspacePlanningProductResult(
    AiDesktopWorkspacePlanningProductStatus Status,
    DesktopWorkspaceFrozenPreview? Preview,
    int DesignAttempts,
    IntelligenceProviderStatus? ProviderStatus,
    int? HttpStatusCode,
    IReadOnlyList<DesktopExplorerWorkspaceGeometryIssue> GeometryIssues,
    IReadOnlyList<DesktopWorkspaceCompileIssue> CompileIssues,
    IReadOnlyList<DesktopWorkspaceEquivalenceIssue> EquivalenceIssues,
    string? Message)
{
    internal bool Succeeded =>
        Status == AiDesktopWorkspacePlanningProductStatus.Success && Preview is not null;
}

/// <summary>
/// ADR-0005 Workspace Blueprint 产品级只读入口。
/// 使用已有同一次 Full Understanding + Observation，生成 Explorer-native Frozen Preview；
/// 当前没有 Candidate、Execution Session、Recovery 或 Writer authority。
/// </summary>
internal sealed class AiDesktopWorkspacePlanningService
{
    internal static readonly TimeSpan ProductDesignTimeout = TimeSpan.FromMinutes(5);

    internal async Task<AiDesktopWorkspacePlanningProductResult> PlanAsync(
        DesktopUnderstandingDraft understanding,
        DesktopObservation observation,
        DesktopLayoutConversationState conversationState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(understanding);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(conversationState);

        var settingsStore = new JsonDesktopProductSettingsStore(
            DesktopAgentDataPaths.DefaultSettingsPath);
        await settingsStore.InitializeAsync(cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);
        var configuredOptions = DesktopProductIntelligenceOptionsFactory.Create(settings);
        if (configuredOptions is null)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.AiDisabled,
                message: "AI 当前已关闭，请先在设置中配置并启用 Provider。",
                providerStatus: IntelligenceProviderStatus.NotConfigured);
        }

        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);
        if (!geometryResult.Succeeded || geometryResult.Geometry is null)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.GeometryUnavailable,
                message: geometryResult.Issues.FirstOrDefault()?.Message ??
                         "当前 DesktopObservation 无法建立可靠的 Explorer-native geometry。",
                geometryIssues: geometryResult.Issues);
        }

        DesktopWorkspaceDesignPreparation preparation;
        try
        {
            preparation = DesktopWorkspaceDesignPreparationFactory.Create(
                understanding,
                observation,
                geometryResult.Geometry,
                conversationState);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.PreparationFailed,
                message: ex.Message);
        }

        var options = new OpenAiCompatibleProviderOptions(
            configuredOptions.BaseUri,
            configuredOptions.Model,
            configuredOptions.SecretReference,
            ProductDesignTimeout);
        using var httpClient = new HttpClient();
        var secretStore = new DpapiIntelligenceSecretStore(
            DesktopAgentDataPaths.DefaultSecretsDirectory);
        IDesktopWorkspaceDesigner designer = new ContractRepairingDesktopWorkspaceDesigner(
            new OpenAiCompatibleDesktopWorkspaceDesigner(
                httpClient,
                options,
                secretStore));
        var coordinator = new DesktopWorkspaceDesignLoopCoordinator(designer);

        var loop = await coordinator.DesignAndCompileAsync(
            preparation.Request,
            preparation.SemanticUnderstanding,
            geometryResult.Geometry,
            preparation.ItemFacts,
            preparation.ItemRefBindings,
            cancellationToken);
        if (!loop.Succeeded)
            return FromLoopFailure(loop);

        try
        {
            var preview = DesktopWorkspaceFrozenPreviewFactory.Create(
                loop.Blueprint!,
                geometryResult.Geometry,
                loop.Plan!,
                loop.Certificate!,
                preparation.SourceSnapshot,
                preparation.ItemRefBindings);
            return new AiDesktopWorkspacePlanningProductResult(
                AiDesktopWorkspacePlanningProductStatus.Success,
                preview,
                loop.DesignAttempts,
                IntelligenceProviderStatus.Success,
                HttpStatusCode: null,
                GeometryIssues: [],
                CompileIssues: [],
                EquivalenceIssues: [],
                Message: null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.PreviewFailed,
                loop.DesignAttempts,
                message: ex.Message);
        }
    }

    private static AiDesktopWorkspacePlanningProductResult FromLoopFailure(
        DesktopWorkspaceDesignLoopResult loop)
        => loop.Status switch
        {
            DesktopWorkspaceDesignLoopStatus.ProviderFailed => Failure(
                AiDesktopWorkspacePlanningProductStatus.ProviderFailed,
                loop.DesignAttempts,
                loop.DiagnosticMessage ?? "Workspace Designer 没有返回可用 Blueprint。",
                loop.ProviderStatus),
            DesktopWorkspaceDesignLoopStatus.CompileBlocked => Failure(
                AiDesktopWorkspacePlanningProductStatus.CompileBlocked,
                loop.DesignAttempts,
                loop.CompileIssues.FirstOrDefault()?.Message ?? "当前本地事实阻塞 Workspace Compile。",
                compileIssues: loop.CompileIssues),
            DesktopWorkspaceDesignLoopStatus.DesignUnresolved => Failure(
                AiDesktopWorkspacePlanningProductStatus.DesignUnresolved,
                loop.DesignAttempts,
                "Workspace Designer 在有界重试预算内仍未形成可忠实落地的 Blueprint。",
                compileIssues: loop.CompileIssues),
            DesktopWorkspaceDesignLoopStatus.EquivalenceFailed => Failure(
                AiDesktopWorkspacePlanningProductStatus.EquivalenceFailed,
                loop.DesignAttempts,
                loop.EquivalenceIssues.FirstOrDefault()?.Message ?? "Exact Plan 未通过独立语义等价验证。",
                equivalenceIssues: loop.EquivalenceIssues),
            _ => Failure(
                AiDesktopWorkspacePlanningProductStatus.PreviewFailed,
                loop.DesignAttempts,
                $"Workspace Design loop 返回未识别状态：{loop.Status}。"),
        };

    private static AiDesktopWorkspacePlanningProductResult Failure(
        AiDesktopWorkspacePlanningProductStatus status,
        int designAttempts = 0,
        string? message = null,
        IntelligenceProviderStatus? providerStatus = null,
        int? httpStatusCode = null,
        IReadOnlyList<DesktopExplorerWorkspaceGeometryIssue>? geometryIssues = null,
        IReadOnlyList<DesktopWorkspaceCompileIssue>? compileIssues = null,
        IReadOnlyList<DesktopWorkspaceEquivalenceIssue>? equivalenceIssues = null)
        => new(
            status,
            Preview: null,
            designAttempts,
            providerStatus,
            httpStatusCode,
            geometryIssues ?? [],
            compileIssues ?? [],
            equivalenceIssues ?? [],
            message);
}
