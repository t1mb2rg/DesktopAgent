namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 解析 AI Provider 所需 Secret 的 UI / 平台无关边界。
/// 持久化配置只保存 SecretReference，不应把实际 API Key 明文写入 SQLite。
/// </summary>
public interface IIntelligenceSecretResolver
{
    ValueTask<string?> ResolveSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default);
}
