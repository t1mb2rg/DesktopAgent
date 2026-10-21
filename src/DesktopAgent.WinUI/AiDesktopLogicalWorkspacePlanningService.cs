using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;
using DesktopAgent.WindowsInterop.Intelligence;

namespace DesktopAgent.WinUI;

internal enum AiDesktopLogicalWorkspacePlanningProductStatus
{
    Success = 0,
    AiDisabled = 1,
    DesktopUnavailable = 2,
    UnderstandingFailure = 3,
    ProjectionFailure = 4,
    PlanningFailure = 5,
}

internal sealed record AiDesktopLogicalWorkspacePlanningProductResult(
    AiDesktopLogicalWorkspacePlanningProductStatus Status,
    DesktopLogicalWorkspacePlanningSession? Session,
    int ObservedItemCount,
    int ProviderAttemptCount,
    IntelligenceProviderStatus? ProviderStatus,
    int? HttpStatusCode,
    IReadOnlyList<DesktopLogicalWorkspaceProjectionIssue> ProjectionIssues,
    string? Message)
{
    internal bool Succeeded =>
        Status == AiDesktopLogicalWorkspacePlanningProductStatus.Success && Session is not null;
}

/// <summary>
/// 新逻辑工作区架构的产品级只读规划入口。
/// 它读取一次 fresh DesktopObservation，并返回已严格验证的不透明 PlanningSession；
/// 当前不接入 AI Assistant UI、旧 Draft Compiler、Preview、Execution Planner 或 Writer。
/// </summary>
internal sealed class AiDesktopLogicalWorkspacePlanningService
{
    internal static readonly TimeSpan ProductPlanningTimeout = TimeSpan.FromMinutes(5);

    internal async Task<AiDesktopLogicalWorkspacePlanningProductResult> PlanAsync(
        string userGuidance,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userGuidance))
            throw new ArgumentException("UserGuidance 不能为空。", nameof(userGuidance));

        var settingsStore = new JsonDesktopProductSettingsStore(
            DesktopAgentDataPaths.DefaultSettingsPath);
        await settingsStore.InitializeAsync(cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);

        var configuredOptions = DesktopProductIntelligenceOptionsFactory.Create(settings);
        if (configuredOptions is null)
        {
            return new AiDesktopLogicalWorkspacePlanningProductResult(
                AiDesktopLogicalWorkspacePlanningProductStatus.AiDisabled,
                Session: null,
                ObservedItemCount: 0,
                ProviderAttemptCount: 0,
                ProviderStatus: IntelligenceProviderStatus.NotConfigured,
                HttpStatusCode: null,
                ProjectionIssues: [],
                Message: "AI 当前已关闭，请先在设置中配置并启用 Provider。");
        }

        using var staExecutor = new StaThreadExecutor();
        var observationSource = new WindowsDesktopObservationSource(
            new NativeDisplayTopologyReader(),
            new DesktopShellIconReader(),
            new DesktopShellIconPositioningCapabilitiesReader(),
            staExecutor);
        var capture = await observationSource.CaptureAsync(cancellationToken);
        if (!capture.IsCaptured || capture.Observation is null)
        {
            return new AiDesktopLogicalWorkspacePlanningProductResult(
                AiDesktopLogicalWorkspacePlanningProductStatus.DesktopUnavailable,
                Session: null,
                ObservedItemCount: 0,
                ProviderAttemptCount: 0,
                ProviderStatus: null,
                HttpStatusCode: null,
                ProjectionIssues: [],
                Message: capture.Issues.FirstOrDefault()?.Message ?? "当前桌面暂时不可读取。");
        }

        var observation = capture.Observation;
        if (observation.Icons.Count == 0)
        {
            return new AiDesktopLogicalWorkspacePlanningProductResult(
                AiDesktopLogicalWorkspacePlanningProductStatus.DesktopUnavailable,
                Session: null,
                ObservedItemCount: 0,
                ProviderAttemptCount: 0,
                ProviderStatus: null,
                HttpStatusCode: null,
                ProjectionIssues: [],
                Message: "当前桌面没有可供 AI 规划的项目。");
        }

        if (observation.Icons.Count > DesktopFirstRunUnderstandingRequest.MaxItems)
        {
            return new AiDesktopLogicalWorkspacePlanningProductResult(
                AiDesktopLogicalWorkspacePlanningProductStatus.DesktopUnavailable,
                Session: null,
                ObservedItemCount: observation.Icons.Count,
                ProviderAttemptCount: 0,
                ProviderStatus: null,
                HttpStatusCode: null,
                ProjectionIssues: [],
                Message: $"当前桌面有 {observation.Icons.Count} 个项目，超过单轮语义理解上限 {DesktopFirstRunUnderstandingRequest.MaxItems}。");
        }

        var options = new OpenAiCompatibleProviderOptions(
            configuredOptions.BaseUri,
            configuredOptions.Model,
            configuredOptions.SecretReference,
            ProductPlanningTimeout);
        using var httpClient = new HttpClient();
        var secretStore = new DpapiIntelligenceSecretStore(
            DesktopAgentDataPaths.DefaultSecretsDirectory);
        var (_, existingZones) = await AiDesktopUnderstandingService.LoadExistingZonesAsync(
            settings.ActiveLayoutTemplateId,
            cancellationToken);
        var understandingRequest = DesktopObservationUnderstandingRequestFactory.Create(
            observation,
            existingZones,
            currentIntentSummary: null,
            latestUserGuidance: userGuidance);
        var understandingProvider = new OpenAiCompatibleDesktopFirstRunIntelligenceProvider(
            httpClient,
            options,
            secretStore);
        var understanding = await understandingProvider
            .UnderstandAsync(understandingRequest, cancellationToken);
        if (understanding.Status != IntelligenceProviderStatus.Success ||
            understanding.Draft is null)
        {
            return new AiDesktopLogicalWorkspacePlanningProductResult(
                AiDesktopLogicalWorkspacePlanningProductStatus.UnderstandingFailure,
                Session: null,
                ObservedItemCount: observation.Icons.Count,
                ProviderAttemptCount: 0,
                ProviderStatus: understanding.Status,
                HttpStatusCode: understanding.HttpStatusCode,
                ProjectionIssues: [],
                Message: understanding.DiagnosticMessage ??
                         "AI 未能为当前 Observation 建立可靠的语义分类。");
        }

        var planner = new OpenAiCompatibleDesktopLogicalWorkspacePlanner(
            httpClient,
            options,
            secretStore);
        var coordinator = new DesktopLogicalWorkspacePlanningSessionCoordinator(planner);

        var result = await coordinator.PlanAsync(
            observation,
            understanding.Draft,
            userGuidance,
            cancellationToken);
        if (result.Status == DesktopLogicalWorkspacePlanningSessionStatus.ProjectionFailed)
        {
            return new AiDesktopLogicalWorkspacePlanningProductResult(
                AiDesktopLogicalWorkspacePlanningProductStatus.ProjectionFailure,
                Session: null,
                ObservedItemCount: observation.Icons.Count,
                ProviderAttemptCount: 0,
                ProviderStatus: null,
                HttpStatusCode: null,
                ProjectionIssues: result.ProjectionIssues,
                Message: result.ProjectionIssues.FirstOrDefault()?.Message ??
                         "当前桌面无法建立可靠的逻辑网格。");
        }

        if (!result.Succeeded)
        {
            var planningRun = result.PlanningRun!;
            var providerResult = planningRun.FinalResult;
            return new AiDesktopLogicalWorkspacePlanningProductResult(
                AiDesktopLogicalWorkspacePlanningProductStatus.PlanningFailure,
                Session: null,
                ObservedItemCount: observation.Icons.Count,
                ProviderAttemptCount: planningRun.AttemptCount,
                ProviderStatus: providerResult.Status,
                HttpStatusCode: providerResult.HttpStatusCode,
                ProjectionIssues: [],
                Message: providerResult.DiagnosticMessage ??
                         "AI 终态规划未通过本地严格校验。");
        }

        return new AiDesktopLogicalWorkspacePlanningProductResult(
            AiDesktopLogicalWorkspacePlanningProductStatus.Success,
            result.Session,
            observation.Icons.Count,
            result.Session!.ProviderAttemptCount,
            IntelligenceProviderStatus.Success,
            HttpStatusCode: null,
            ProjectionIssues: [],
            Message: null);
    }
}
