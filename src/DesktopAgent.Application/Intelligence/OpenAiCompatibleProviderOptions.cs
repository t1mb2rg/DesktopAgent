namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// DesktopAgent v1.0 使用的保守 OpenAI-compatible Provider 配置。
/// BaseUri 例如 https://api.deepseek.com 或 http://127.0.0.1:8000/v1。
/// 实际 API Key 通过 SecretReference 在调用时解析。
/// </summary>
public sealed class OpenAiCompatibleProviderOptions
{
    public Uri BaseUri { get; }
    public string Model { get; }
    public string SecretReference { get; }
    public TimeSpan Timeout { get; }

    public OpenAiCompatibleProviderOptions(
        Uri baseUri,
        string model,
        string secretReference,
        TimeSpan? timeout = null)
    {
        BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        if (!BaseUri.IsAbsoluteUri)
            throw new ArgumentException("BaseUri 必须是绝对 URI。", nameof(baseUri));
        if (BaseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("BaseUri 只支持 http 或 https。", nameof(baseUri));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model 不能为空。", nameof(model));
        if (string.IsNullOrWhiteSpace(secretReference))
            throw new ArgumentException("SecretReference 不能为空。", nameof(secretReference));

        var resolvedTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (resolvedTimeout <= TimeSpan.Zero || resolvedTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout 必须大于 0 且不能无限等待。");

        Model = model;
        SecretReference = secretReference;
        Timeout = resolvedTimeout;
    }
}
