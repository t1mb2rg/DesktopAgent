namespace DesktopAgent.Core.Productization;

/// <summary>
/// 产品设置持久化边界。实现只能持久化非秘密配置；真实 API Key 由 Secret Store 管理。
/// </summary>
public interface IDesktopProductSettingsStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<DesktopProductSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        DesktopProductSettings settings,
        CancellationToken cancellationToken = default);
}
