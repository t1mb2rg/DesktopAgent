using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
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
    int AnalyzedItemCount,
    string? ActiveLayoutName,
    IntelligenceProviderStatus? ProviderStatus,
    int? HttpStatusCode,
    string? Message)
{
    internal bool Succeeded =>
        Status == AiDesktopUnderstandingProductStatus.Success && Draft is not null;
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
/// M5-07.6 产品接入层：把 fresh read-only DesktopObservation 转换为 M4-02
/// DesktopFirstRunUnderstandingRequest，并调用既有 OpenAI-compatible Provider。
/// 这里只形成语义 Draft，不构造 Planner / Coordinator / Writer，也不产生物理坐标副作用。
/// </summary>
internal sealed class AiDesktopUnderstandingService
{
    internal static readonly TimeSpan ProductUnderstandingTimeout = TimeSpan.FromSeconds(90);
    internal const int ProductUnderstandingSampleLimit = 32;

    /// <summary>
    /// 轻量 Provider 探针。只复用现有 M4 分类 Provider 验证 BaseUri / Model / Secret /
    /// OpenAI-compatible JSON transport，不读取真实桌面，也不触碰 Writer。
    /// </summary>
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
                AnalyzedItemCount: 0,
                ActiveLayoutName: null,
                ProviderStatus: null,
                HttpStatusCode: null,
                Message: "AI 当前已关闭，请先在设置中配置并启用 Provider。");
        }

        // M4 Provider 默认 30 秒保持冻结；整桌理解属于天然重请求，
        // 仅在产品组合层为这一条链路提供更宽的有界预算。
        var options = new OpenAiCompatibleProviderOptions(
            configuredOptions.BaseUri,
            configuredOptions.Model,
            configuredOptions.SecretReference,
            ProductUnderstandingTimeout);

        using var staExecutor = new StaThreadExecutor();
        var observationSource = new WindowsDesktopObservationSource(
            new NativeDisplayTopologyReader(),
            new DesktopShellIconReader(),
            new DesktopShellIconPositioningCapabilitiesReader(),
            staExecutor);

        var capture = await observationSource.CaptureAsync(cancellationToken);
        if (!capture.IsCaptured || capture.Observation is null)
        {
            return new AiDesktopUnderstandingProductResult(
                AiDesktopUnderstandingProductStatus.DesktopUnavailable,
                Draft: null,
                ObservedItemCount: 0,
                AnalyzedItemCount: 0,
                ActiveLayoutName: null,
                ProviderStatus: null,
                HttpStatusCode: null,
                Message: capture.Issues.FirstOrDefault()?.Message ?? "当前桌面暂时不可读取。");
        }

        var icons = capture.Observation.Icons;
        if (icons.Count == 0)
        {
            return new AiDesktopUnderstandingProductResult(
                AiDesktopUnderstandingProductStatus.DesktopUnavailable,
                Draft: null,
                ObservedItemCount: 0,
                AnalyzedItemCount: 0,
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
                AnalyzedItemCount: 0,
                ActiveLayoutName: null,
                ProviderStatus: null,
                HttpStatusCode: null,
                Message: $"当前桌面有 {icons.Count} 个项目，超过单轮 AI 理解上限 {DesktopFirstRunUnderstandingRequest.MaxItems}。请先减少桌面项目后重试。");
        }

        var observedFacts = icons.Select(ToFacts).ToArray();
        var facts = BuildRepresentativeSample(observedFacts);
        var (activeLayoutName, existingZones) = await LoadExistingZonesAsync(
            settings.ActiveLayoutTemplateId,
            cancellationToken);

        var request = new DesktopFirstRunUnderstandingRequest(
            facts,
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
                ObservedItemCount: observedFacts.Length,
                AnalyzedItemCount: facts.Length,
                ActiveLayoutName: activeLayoutName,
                ProviderStatus: providerResult.Status,
                HttpStatusCode: providerResult.HttpStatusCode,
                Message: providerResult.DiagnosticMessage);
        }

        return new AiDesktopUnderstandingProductResult(
            AiDesktopUnderstandingProductStatus.Success,
            providerResult.Draft,
            observedFacts.Length,
            facts.Length,
            activeLayoutName,
            IntelligenceProviderStatus.Success,
            HttpStatusCode: null,
            Message: null);
    }

    private static DesktopItemFacts[] BuildRepresentativeSample(
        IReadOnlyList<DesktopItemFacts> items)
    {
        if (items.Count <= ProductUnderstandingSampleLimit)
            return items.ToArray();

        // 仅在本地按“项目类型 + 扩展名 + 系统项目标志”分桶，再轮询取样。
        // 这样既能覆盖不同文件类型，也不会把某一种大量项目完全挤掉。
        var queues = items
            .GroupBy(static item => (
                item.ItemKind,
                Extension: (item.FileExtension ?? string.Empty).ToUpperInvariant(),
                item.IsSystemItem))
            .OrderByDescending(static group => group.Key.IsSystemItem)
            .ThenByDescending(static group => group.Count())
            .ThenBy(static group => group.Key.ItemKind)
            .ThenBy(static group => group.Key.Extension, StringComparer.Ordinal)
            .Select(static group => new Queue<DesktopItemFacts>(
                group
                    .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.StableId, StringComparer.Ordinal)))
            .ToList();

        var selected = new List<DesktopItemFacts>(ProductUnderstandingSampleLimit);
        while (selected.Count < ProductUnderstandingSampleLimit)
        {
            var tookAny = false;
            foreach (var queue in queues)
            {
                if (queue.Count == 0)
                    continue;

                selected.Add(queue.Dequeue());
                tookAny = true;
                if (selected.Count == ProductUnderstandingSampleLimit)
                    break;
            }

            if (!tookAny)
                break;
        }

        return selected.ToArray();
    }

    private static DesktopItemFacts ToFacts(DesktopIconDescriptor icon)
        => new(
            icon.StableId,
            icon.DisplayName,
            icon.ItemKind,
            TryGetFileExtension(icon),
            icon.IsSystemItem,
            icon.IsFileSystemItem);

    private static string? TryGetFileExtension(DesktopIconDescriptor icon)
    {
        if (!icon.IsFileSystemItem ||
            icon.ItemKind is not (ItemKind.File or ItemKind.Shortcut) ||
            string.IsNullOrWhiteSpace(icon.ParsingName))
        {
            return null;
        }

        try
        {
            var extension = Path.GetExtension(icon.ParsingName);
            return string.IsNullOrEmpty(extension) ? null : extension;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<(string? LayoutName, IReadOnlyList<IntelligenceZoneOption> Zones)>
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
