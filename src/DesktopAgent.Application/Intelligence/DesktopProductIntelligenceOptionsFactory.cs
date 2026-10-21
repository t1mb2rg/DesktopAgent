using DesktopAgent.Core.Productization;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 把 M5 产品设置桥接到 M4 已冻结的 OpenAI-compatible Provider 合同。
/// AI Disabled 返回 null，不隐式创建或调用 Provider。
/// </summary>
public static class DesktopProductIntelligenceOptionsFactory
{
    public static OpenAiCompatibleProviderOptions? Create(DesktopProductSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.AiEnabled)
            return null;

        if (settings.AiBaseUri is null || settings.AiModel is null)
            throw new InvalidOperationException("AI 已启用，但 Provider 配置不完整。");

        return new OpenAiCompatibleProviderOptions(
            settings.AiBaseUri,
            settings.AiModel,
            settings.AiSecretReference);
    }
}
