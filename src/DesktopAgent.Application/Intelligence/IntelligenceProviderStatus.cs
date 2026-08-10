namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// AI Provider 单次调用的应用层结果状态。
/// 这些状态只描述智能能力是否可用，不表示任何桌面写入许可。
/// </summary>
public enum IntelligenceProviderStatus
{
    Success = 0,
    NotConfigured = 1,
    Unavailable = 2,
    Timeout = 3,
    Cancelled = 4,
    InvalidResponse = 5,
}
