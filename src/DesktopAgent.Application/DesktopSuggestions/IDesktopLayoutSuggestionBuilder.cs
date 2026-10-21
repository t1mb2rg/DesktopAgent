using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopSuggestions;

/// <summary>
/// M3 Suggestion build port。允许 pipeline 在不知道具体 template/rule store 实现的情况下构建建议，
/// 并为异步 binding-staleness 测试提供可控 seam。
/// </summary>
public interface IDesktopLayoutSuggestionBuilder
{
    Task<DesktopLayoutSuggestionBuildResult> BuildAsync(
        DesktopLayoutSuggestionBinding binding,
        DesktopStateChangeSet changeSet,
        DesktopObservation currentObservation,
        CancellationToken ct = default);
}
