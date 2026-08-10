using DesktopAgent.Core.Automation;

namespace DesktopAgent.Core.Productization;

/// <summary>
/// 产品壳拥有的非秘密设置。API Key 本体绝不能进入此模型或普通持久化配置。
/// </summary>
public sealed class DesktopProductSettings
{
    public DesktopAutomationMode AutomationMode { get; }
    public bool FirstRunCompleted { get; }
    public bool AiEnabled { get; }
    public Uri? AiBaseUri { get; }
    public string? AiModel { get; }
    public string? AiSecretReference { get; }

    public DesktopProductSettings(
        DesktopAutomationMode automationMode,
        bool firstRunCompleted,
        bool aiEnabled,
        Uri? aiBaseUri,
        string? aiModel,
        string? aiSecretReference)
    {
        if (!Enum.IsDefined(automationMode))
            throw new ArgumentOutOfRangeException(nameof(automationMode));

        if (aiBaseUri is not null)
        {
            if (!aiBaseUri.IsAbsoluteUri)
                throw new ArgumentException("AI BaseUri 必须是绝对 URI。", nameof(aiBaseUri));
            if (aiBaseUri.Scheme is not ("http" or "https"))
                throw new ArgumentException("AI BaseUri 只支持 http 或 https。", nameof(aiBaseUri));
            if (!string.IsNullOrEmpty(aiBaseUri.UserInfo))
                throw new ArgumentException("AI BaseUri 不能内嵌用户名或密码。", nameof(aiBaseUri));
            if (!string.IsNullOrEmpty(aiBaseUri.Query) || !string.IsNullOrEmpty(aiBaseUri.Fragment))
                throw new ArgumentException("AI BaseUri 不能包含 query 或 fragment。", nameof(aiBaseUri));
        }

        var normalizedModel = aiModel?.Trim();
        if (normalizedModel is not null && normalizedModel.Length == 0)
            throw new ArgumentException("AiModel 应使用 null 表示未配置，不能是空白。", nameof(aiModel));

        var normalizedSecretReference = aiSecretReference?.Trim();
        if (normalizedSecretReference is not null && normalizedSecretReference.Length == 0)
            throw new ArgumentException("AiSecretReference 应使用 null 表示无认证，不能是空白。", nameof(aiSecretReference));

        if ((aiBaseUri is null) != (normalizedModel is null))
            throw new ArgumentException("AI BaseUri 与 Model 必须同时配置或同时为空。", nameof(aiModel));

        if (normalizedSecretReference is not null && aiBaseUri is null)
            throw new ArgumentException("存在 SecretReference 时必须同时配置 AI Provider。", nameof(aiSecretReference));

        if (aiEnabled && aiBaseUri is null)
            throw new ArgumentException("启用 AI 前必须先配置 BaseUri 与 Model。", nameof(aiEnabled));

        AutomationMode = automationMode;
        FirstRunCompleted = firstRunCompleted;
        AiEnabled = aiEnabled;
        AiBaseUri = aiBaseUri;
        AiModel = normalizedModel;
        AiSecretReference = normalizedSecretReference;
    }

    public static DesktopProductSettings CreateDefault()
        => new(
            DesktopAutomationPolicy.DefaultMode,
            firstRunCompleted: false,
            aiEnabled: false,
            aiBaseUri: null,
            aiModel: null,
            aiSecretReference: null);
}
