using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>
/// Workspace Blueprint 设计可复用上次完整理解的语义，但物理起点必须重新观察。
/// 只有项目语义身份和显示器拓扑仍然匹配，才能以新 Observation 生成新的 Exact Plan。
/// 本门只是设计输入门：绝不生成 Apply Confirmation 或 Writer authority。
/// </summary>
public static class DesktopWorkspacePlanningObservationGate
{
    public static DesktopWorkspacePlanningObservationResult Evaluate(
        DesktopObservation understoodObservation,
        DesktopObservation freshObservation)
    {
        ArgumentNullException.ThrowIfNull(understoodObservation);
        ArgumentNullException.ThrowIfNull(freshObservation);

        if (!string.Equals(
                DesktopSemanticUnderstandingSourceFingerprint.Create(understoodObservation),
                DesktopSemanticUnderstandingSourceFingerprint.Create(freshObservation),
                StringComparison.Ordinal))
        {
            return new(false, "桌面项目的数量、名称或类型已变化。请先点击“重新理解桌面”，不要用旧语义生成新预览。");
        }

        var previousDisplays = understoodObservation.Displays
            .OrderBy(static display => display.DeviceName, StringComparer.Ordinal)
            .ToArray();
        var currentDisplays = freshObservation.Displays
            .OrderBy(static display => display.DeviceName, StringComparer.Ordinal)
            .ToArray();

        if (previousDisplays.Length != currentDisplays.Length ||
            !previousDisplays.SequenceEqual(currentDisplays))
        {
            return new(false, "显示器身份、分辨率、缩放或工作区已变化。请先点击“重新理解桌面”，再生成精确预览。");
        }

        var previousCapabilities = understoodObservation.PositioningCapabilities;
        var currentCapabilities = freshObservation.PositioningCapabilities;
        if (previousCapabilities.IsAutoArrangeEnabled != currentCapabilities.IsAutoArrangeEnabled ||
            previousCapabilities.Spacing != currentCapabilities.Spacing)
        {
            return new(false, "Explorer 图标排列能力或网格间距已变化。请重新理解桌面并校准网格后再生成预览。");
        }

        // 位置或跨屏归属可以变化：语义仍有效，但旧物理起点无效。
        // 新 Exact Plan 必须使用调用方刚刚 Capture 的 freshObservation。
        return new(true, null);
    }
}

public sealed record DesktopWorkspacePlanningObservationResult(bool CanReuseUnderstanding, string? BlockReason);
