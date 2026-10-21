using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 用首次 Full Understanding + 同一次本地 Observation 建立后续轻量布局对话上下文。
/// 不读取新物理坐标，不向 Provider 暴露 DeviceName / StableId。
/// </summary>
public static class DesktopLayoutConversationContextBuilder
{
    public static DesktopLayoutConversationRequest Build(
        DesktopUnderstandingDraft understanding,
        DesktopObservation baseObservation,
        DesktopLayoutConversationState currentState,
        string latestUserGuidance)
    {
        ArgumentNullException.ThrowIfNull(understanding);
        ArgumentNullException.ThrowIfNull(baseObservation);
        ArgumentNullException.ThrowIfNull(currentState);

        var displayMap = DesktopSemanticDisplayMap.Create(baseObservation);
        var iconByStableId = baseObservation.Icons.ToDictionary(
            static icon => icon.StableId,
            StringComparer.Ordinal);
        var assignmentByStableId = understanding.Assignments.ToDictionary(
            static assignment => assignment.StableId,
            StringComparer.Ordinal);

        var items = new List<DesktopLayoutConversationItemContext>(understanding.Assignments.Count);
        foreach (var assignment in understanding.Assignments)
        {
            if (!iconByStableId.TryGetValue(assignment.StableId, out var icon))
            {
                throw new InvalidOperationException(
                    $"语义模型中的 StableId \"{assignment.StableId}\" 已不在首次 Observation 中。请重新理解桌面。");
            }

            if (!displayMap.TryResolveDeviceName(icon.MonitorDeviceName, out var displayEntry)
                || displayEntry is null)
            {
                throw new InvalidOperationException(
                    $"项目 \"{icon.DisplayName}\" 没有可证明的语义显示器归属。请重新理解桌面。");
            }

            items.Add(new DesktopLayoutConversationItemContext(
                assignment.StableId,
                icon.DisplayName,
                assignment.CategoryKey,
                displayEntry.DisplayRef));
        }

        // 防止未来调用方传入有重复/缺失 Assignment 的非标准 Draft 后静默进入轻量会话。
        if (items.Count != assignmentByStableId.Count)
            throw new InvalidOperationException("首次语义模型的 Assignment StableId 不唯一，不能建立轻量布局对话。 ");

        return new DesktopLayoutConversationRequest(
            understanding,
            displayMap.ToProviderContexts(),
            items,
            currentState,
            latestUserGuidance);
    }
}
