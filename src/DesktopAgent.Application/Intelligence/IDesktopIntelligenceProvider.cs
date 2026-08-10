namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// M4 智能分类 Provider 抽象。
/// Provider 只能返回语义 Suggestion，不能表达物理坐标、Writer command 或 Shell 副作用。
/// </summary>
public interface IDesktopIntelligenceProvider
{
    Task<IntelligenceProviderResult> ClassifyAsync(
        DesktopItemClassificationRequest request,
        CancellationToken cancellationToken = default);
}
