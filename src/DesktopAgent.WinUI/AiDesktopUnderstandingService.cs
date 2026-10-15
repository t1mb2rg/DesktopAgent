using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.Intelligence;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;
using DesktopAgent.WindowsInterop.Intelligence;

namespace DesktopAgent.WinUI;

internal enum AiDesktopUnderstandingProductStatus
{
    Success = 0,
    AiDisabled = 1,
    DesktopUnavailable = 2,
    ProviderFailure = 3,
}

internal sealed record AiDesktopUnderstandingProductResult(
    AiDesktopUnderstandingProductStatus Status,
    DesktopUnderstandingDraft? Draft,
    int ObservedItemCount,
    string? ActiveLayoutName,
    IntelligenceProviderStatus? ProviderStatus,
    int? HttpStatusCode,
    string? Message,
    DesktopObservation? Observation = null,
    string? PersistenceWarning = null)
{
    internal bool Succeeded =>
        Status == AiDesktopUnderstandingProductStatus.Success && Draft is not null;
}

internal enum AiPersistedUnderstandingRestoreStatus
{
    None = 0,
    Restored = 1,
    DesktopUnavailable = 2,
    PersistenceFailure = 3,
}

internal sealed record AiPersistedUnderstandingProductState(
    AiPersistedUnderstandingRestoreStatus Status,
    DesktopSemanticUnderstandingSnapshot? Snapshot,
    DesktopSemanticUnderstandingFreshnessResult? Freshness,
    DesktopObservation? CurrentObservation,
    string? Message)
{
    internal bool HasPersistedUnderstanding => Snapshot is not null;
    internal bool IsCurrent =>
        Status == AiPersistedUnderstandingRestoreStatus.Restored &&
        Freshness?.Freshness == DesktopSemanticUnderstandingFreshness.Current &&
        CurrentObservation is not null;
}

internal sealed record AiProviderProbeProductResult(
    IntelligenceProviderStatus Status,
    string? Category,
    int? HttpStatusCode,
    string? Message)
{
    internal bool Succeeded => Status == IntelligenceProviderStatus.Success;
}

/// <summary>
/// 产品 AI 理解入口。Full Understanding 使用 fresh read-only DesktopObservation 调用 Provider；
/// 成功后把纯 Semantic Understanding 持久化为 V8 durable snapshot。
/// 启动恢复只读取本地 snapshot + fresh Observation 来计算 freshness，不依赖 Provider 在线，
/// 也不恢复 Preview、Confirmation 或 Writer authority。
/// </summary>
internal sealed class AiDesktopUnderstandingService
{
    internal static readonly TimeSpan ProductUnderstandingTimeout = TimeSpan.FromMinutes(5);

    internal async Task<AiProviderProbeProductResult> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var settingsStore = new JsonDesktopProductSettingsStore(
            DesktopAgentDataPaths.DefaultSettingsPath);
        await settingsStore.InitializeAsync(cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);

        var options = DesktopProductIntelligenceOptionsFactory.Create(settings);
        if (options is null)
        {
            return new AiProviderProbeProductResult(
                IntelligenceProviderStatus.NotConfigured,
                Category: null,
                HttpStatusCode: null,
                Message: "AI 当前已关闭，请先在设置中配置并启用 Provider。");
        }

        using var httpClient = new HttpClient();
        var secretStore = new DpapiIntelligenceSecretStore(
            DesktopAgentDataPaths.DefaultSecretsDirectory);
        var provider = new OpenAiCompatibleDesktopIntelligenceProvider(
            httpClient,
            options,
            secretStore);

        var request = new DesktopItemClassificationRequest(
            new DesktopItemFacts(
                "tessel-provider-probe",
                "Tessel Provider Probe.txt",
                ItemKind.File,
                ".txt",
                isSystemItem: false,
                isFileSystemItem: true));

        var result = await provider.ClassifyAsync(request, cancellationToken);
        return new AiProviderProbeProductResult(
            result.Status,
            result.Suggestion?.Category,
            result.HttpStatusCode,
            result.DiagnosticMessage);
    }

    internal async Task<AiPersistedUnderstandingProductState> RestoreLatestAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var store = new SqliteDesktopSemanticUnderstandingStore(
                DesktopAgentDataPaths.DefaultDatabasePath);
            await store.InitializeAsync(cancellationToken);
            var snapshot = await store.GetLatestAsync(cancellationToken);
            if (snapshot is null)
            {
                return new AiPersistedUnderstandingProductState(
                    AiPersistedUnderstandingRestoreStatus.None,
                    Snapshot: null,
                    Freshness: null,
                    CurrentObservation: null,
                    Message: null);
            }

            var capture = await CaptureObservationAsync(cancellationToken);
            if (!capture.IsCaptured || capture.Observation is null || capture.Observation.Icons.Count == 0)
            {
                return new AiPersistedUnderstandingProductState(
                    AiPersistedUnderstandingRestoreStatus.DesktopUnavailable,
                    snapshot,
                    Freshness: null,
                    CurrentObservation: null,
                    capture.Issues.FirstOrDefault()?.Message ?? "当前桌面暂时不可读取，无法检查已保存理解是否仍然新鲜。");
            }

            var freshness = DesktopSemanticUnderstandingFreshnessEvaluator.Evaluate(
                snapshot,
                capture.Observation);
            return new AiPersistedUnderstandingProductState(
                AiPersistedUnderstandingRestoreStatus.Restored,
                snapshot,
                freshness,
                capture.Observation,
                Message: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiPersistedUnderstandingProductState(
                AiPersistedUnderstandingRestoreStatus.PersistenceFailure,
                Snapshot: null,
                Freshness: null,
                CurrentObservation: null,
                $"已保存的桌面理解无法安全恢复：{ex.Message}");
        }
    }

    internal async Task<AiDesktopUnderstandingProductResult> AnalyzeAsync(
        string? previousIntentSummary,
        string? latestUserGuidance,
        CancellationToken cancellationToken = default)
    {
        var settingsStore = new JsonDesktopProductSettingsStore(
            DesktopAgentDataPaths.DefaultSettingsPath);
        await settingsStore.InitializeAsync(cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);

        var configuredOptions = DesktopProductIntelligenceOptionsFactory.Create(settings);
        if (configuredOptions is null)
        {
            return new AiDesktopUnderstandingProductResult(
                AiDesktopUnderstandingProductStatus.AiDisabled,
                Draft: null,
                ObservedItemCount: 0,
                ActiveLayoutName: null,
                ProviderStatus: null,
                HttpStatusCode: null,
                Message: "AI 当前已关闭，请先在设置中配置并启用 Provider。");
        }

        var options = new OpenAiCompatibleProviderOptions(
            configuredOptions.BaseUri,
            configuredOptions.Model,
            configuredOptions.SecretReference,
            ProductUnderstandingTimeout);

        var capture = await CaptureObservationAsync(cancellationToken);
        if (!capture.IsCaptured || capture.Observation is null)
        {
            return new AiDesktopUnderstandingProductResult(
                AiDesktopUnderstandingProductStatus.DesktopUnavailable,
                Draft: null,
                ObservedItemCount: 0,
                ActiveLayoutName: null,
                ProviderStatus: null,
                HttpStatusCode: null,
                Message: capture.Issues.FirstOrDefault()?.Message ?? "当前桌面暂时不可读取。");
        }

        var observation = capture.Observation;
        var icons = observation.Icons;
        if (icons.Count == 0)
        {
            return new AiDesktopUnderstandingProductResult(
                AiDesktopUnderstandingProductStatus.DesktopUnavailable,
                Draft: null,
                ObservedItemCount: 0,
                ActiveLayoutName: null,
                ProviderStatus: null,
                HttpStatusCode: null,
                Message: "当前桌面没有可供 AI 理解的项目。");
        }

        if (icons.Count > DesktopFirstRunUnderstandingRequest.MaxItems)
        {
            return new AiDesktopUnderstandingProductResult(
                AiDesktopUnderstandingProductStatus.DesktopUnavailable,
                Draft: null,
                ObservedItemCount: icons.Count,
                ActiveLayoutName: null,
                ProviderStatus: null,
                HttpStatusCode: null,
                Message: $"当前桌面有 {icons.Count} 个项目，超过单轮 AI 理解上限 {DesktopFirstRunUnderstandingRequest.MaxItems}。请先减少桌面项目后重试。");
        }

        var (activeLayoutName, existingZones) = await LoadExistingZonesAsync(
            settings.ActiveLayoutTemplateId,
            cancellationToken);

        var request = DesktopObservationUnderstandingRequestFactory.Create(
            observation,
            existingZones,
            previousIntentSummary,
            latestUserGuidance);

        using var httpClient = new HttpClient();
        var secretStore = new DpapiIntelligenceSecretStore(
            DesktopAgentDataPaths.DefaultSecretsDirectory);
        var provider = new OpenAiCompatibleDesktopFirstRunIntelligenceProvider(
            httpClient,
            options,
            secretStore);

        var providerResult = await provider.UnderstandAsync(request, cancellationToken);
        if (providerResult.Status != IntelligenceProviderStatus.Success ||
            providerResult.Draft is null)
        {
            return new AiDesktopUnderstandingProductResult(
                AiDesktopUnderstandingProductStatus.ProviderFailure,
                Draft: null,
                ObservedItemCount: icons.Count,
                ActiveLayoutName: activeLayoutName,
                ProviderStatus: providerResult.Status,
                HttpStatusCode: providerResult.HttpStatusCode,
                Message: providerResult.DiagnosticMessage);
        }

        string? persistenceWarning = null;
        try
        {
            await PersistSuccessfulUnderstandingAsync(
                providerResult.Draft,
                observation,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            persistenceWarning = $"理解已建立，但未能写入长期记忆：{ex.Message}";
        }

        return new AiDesktopUnderstandingProductResult(
            AiDesktopUnderstandingProductStatus.Success,
            providerResult.Draft,
            icons.Count,
            activeLayoutName,
            IntelligenceProviderStatus.Success,
            HttpStatusCode: null,
            Message: null,
            Observation: observation,
            PersistenceWarning: persistenceWarning);
    }

    private static async Task PersistSuccessfulUnderstandingAsync(
        DesktopUnderstandingDraft draft,
        DesktopObservation observation,
        CancellationToken cancellationToken)
    {
        var semantic = DesktopSemanticUnderstandingProjector.FromLegacyDraft(draft);
        var semanticIds = semantic.Assignments
            .Select(static assignment => assignment.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var observationIds = observation.Icons
            .Select(static icon => icon.StableId)
            .ToHashSet(StringComparer.Ordinal);
        if (!semanticIds.SetEquals(observationIds))
            throw new InvalidDataException("成功的 Full Understanding 未完整覆盖来源 Observation，拒绝写入长期语义记忆。");

        var snapshot = new DesktopSemanticUnderstandingSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            observation.ObservationId,
            DesktopSemanticUnderstandingSourceFingerprint.Create(observation),
            observation.Icons.Count,
            semantic);
        var store = new SqliteDesktopSemanticUnderstandingStore(
            DesktopAgentDataPaths.DefaultDatabasePath);
        await store.InitializeAsync(cancellationToken);
        await store.AddAsync(snapshot, cancellationToken);
    }

    internal static async Task<DesktopObservationCaptureResult> CaptureObservationAsync(
        CancellationToken cancellationToken)
    {
        using var staExecutor = new StaThreadExecutor();
        var observationSource = new WindowsDesktopObservationSource(
            new NativeDisplayTopologyReader(),
            new DesktopShellIconReader(),
            new DesktopShellIconPositioningCapabilitiesReader(),
            staExecutor);
        return await observationSource.CaptureAsync(cancellationToken);
    }

    internal static async Task<(string? LayoutName, IReadOnlyList<IntelligenceZoneOption> Zones)>
        LoadExistingZonesAsync(
            Guid? activeLayoutTemplateId,
            CancellationToken cancellationToken)
    {
        if (activeLayoutTemplateId is not Guid templateId)
            return (null, Array.Empty<IntelligenceZoneOption>());

        var templateStore = new SqliteDesktopLayoutTemplateStore(
            DesktopAgentDataPaths.DefaultDatabasePath);
        await templateStore.InitializeAsync(cancellationToken);
        var template = await templateStore.GetAsync(templateId, cancellationToken);
        if (template is null)
            return (null, Array.Empty<IntelligenceZoneOption>());

        var zones = template.Zones
            .OrderBy(static zone => zone.SortOrder)
            .Select(static zone => new IntelligenceZoneOption(zone.ZoneId, zone.Name))
            .ToArray();
        return (template.Name, Array.AsReadOnly(zones));
    }
}
