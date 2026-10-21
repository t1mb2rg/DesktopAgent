using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.Intelligence;

namespace DesktopAgent.WinUI;

internal sealed record AiDesktopLayoutConversationProductResult(
    IntelligenceProviderStatus Status,
    DesktopLayoutConversationDelta? Delta,
    int? HttpStatusCode,
    string? Message)
{
    internal bool Succeeded => Status == IntelligenceProviderStatus.Success && Delta is not null;
}

/// <summary>
/// 首次完整桌面理解之后的轻量布局对话入口。
/// 不读取 Windows 桌面，不重跑 Full Understanding，只消费已冻结的语义上下文并返回 Layout Delta。
/// </summary>
internal sealed class AiDesktopLayoutConversationService
{
    internal static readonly TimeSpan ProductConversationTimeout = TimeSpan.FromSeconds(75);

    internal async Task<AiDesktopLayoutConversationProductResult> PlanAsync(
        DesktopLayoutConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settingsStore = new JsonDesktopProductSettingsStore(
            DesktopAgentDataPaths.DefaultSettingsPath);
        await settingsStore.InitializeAsync(cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);

        var configuredOptions = DesktopProductIntelligenceOptionsFactory.Create(settings);
        if (configuredOptions is null)
        {
            return new AiDesktopLayoutConversationProductResult(
                IntelligenceProviderStatus.NotConfigured,
                Delta: null,
                HttpStatusCode: null,
                Message: "AI 当前已关闭，请先在设置中配置并启用 Provider。");
        }

        var options = new OpenAiCompatibleProviderOptions(
            configuredOptions.BaseUri,
            configuredOptions.Model,
            configuredOptions.SecretReference,
            ProductConversationTimeout);

        using var httpClient = new HttpClient();
        var secretStore = new DpapiIntelligenceSecretStore(
            DesktopAgentDataPaths.DefaultSecretsDirectory);
        var provider = new OpenAiCompatibleDesktopLayoutConversationProvider(
            httpClient,
            options,
            secretStore);

        var result = await provider.PlanAsync(request, cancellationToken);
        return new AiDesktopLayoutConversationProductResult(
            result.Status,
            result.Delta,
            result.HttpStatusCode,
            result.DiagnosticMessage);
    }
}
