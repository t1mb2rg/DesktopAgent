namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 在现有只读 SecretResolver 之上增加产品设置所需的安全写入/删除能力。
/// 普通配置只能保存 secretReference，不能保存 secret 本体。
/// </summary>
public interface IIntelligenceSecretStore : IIntelligenceSecretResolver
{
    ValueTask SaveSecretAsync(
        string secretReference,
        string secret,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ContainsSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default);
}
