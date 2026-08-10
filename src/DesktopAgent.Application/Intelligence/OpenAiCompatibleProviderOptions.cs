namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// DesktopAgent v1.0 使用的保守 OpenAI-compatible Provider 配置。
/// BaseUri 例如 https://api.deepseek.com 或 http://127.0.0.1:8000/v1。
/// 云端 Provider 可通过 SecretReference 在调用时解析 API Key；本地无认证服务可将其留空。
/// </summary>
public sealed class OpenAiCompatibleProviderOptions
{
    public Uri BaseUri { get; }
    public string Model { get; }
    public string? SecretReference { get; }
    public TimeSpan Timeout { get; }

    public OpenAiCompatibleProviderOptions(
        Uri baseUri,
        string model,
        string? secretReference = null,
        TimeSpan? timeout = null)
    {
        BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        if (!BaseUri.IsAbsoluteUri)
            throw new ArgumentException("BaseUri 必须是绝对 URI。", nameof(baseUri));
        if (BaseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("BaseUri 只支持 http 或 https。", nameof(baseUri));
        if (!string.IsNullOrEmpty(BaseUri.UserInfo))
            throw new ArgumentException("BaseUri 不能内嵌用户名或密码；认证必须通过 SecretReference。", nameof(baseUri));
        if (!string.IsNullOrEmpty(BaseUri.Query) || !string.IsNullOrEmpty(BaseUri.Fragment))
            throw new ArgumentException("BaseUri 不能包含 query 或 fragment。", nameof(baseUri));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model 不能为空。", nameof(model));
        if (secretReference is not null && string.IsNullOrWhiteSpace(secretReference))
            throw new ArgumentException("SecretReference 使用 null 表示无认证，不能使用空白字符串。", nameof(secretReference));

        var resolvedTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (resolvedTimeout <= TimeSpan.Zero ||
            resolvedTimeout == System.Threading.Timeout.InfiniteTimeSpan ||
            resolvedTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout),
                "Timeout 必须在 (0, 5 分钟] 范围内。无界等待不属于 v1.0 Provider 契约。");
        }

        Model = model;
        SecretReference = secretReference;
        Timeout = resolvedTimeout;
    }
}
