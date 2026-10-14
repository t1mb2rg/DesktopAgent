using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 网格事实的拓扑身份：只描述产生事实时的环境，不描述事实本身。
///
/// 必须排除 phase、GridOrigin、BlockedSlotIds。否则判断"存进去的 phase 是否还有效"
/// 需要先知道 phase，形成循环：fingerprint 正是用来校验 phase 的。
///
/// 完整 geometry fingerprint 继续作为 plan 与 Realization Certificate 的 identity；
/// 两者不能混用。
/// </summary>
public static class ExplorerGridTopologyIdentity
{
    public static string Compute(
        bool isAutoArrangeEnabled,
        PixelPoint spacing,
        IReadOnlyList<DisplayDescriptor> displays)
    {
        if (spacing.X <= 0 || spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing 必须为正数。");
        if (displays.Count == 0)
            throw new ArgumentException("拓扑身份至少需要一块显示器。", nameof(displays));

        var builder = new StringBuilder();
        builder.Append($"auto={(isAutoArrangeEnabled ? 1 : 0)}|spacing={spacing.X}:{spacing.Y}\n");

        foreach (var display in displays.OrderBy(static display => display.DeviceName, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(display.DeviceName))
                throw new ArgumentException("DisplayDescriptor.DeviceName 不能为空。", nameof(displays));

            builder.Append(
                $"{display.DeviceName}|{display.Bounds.Left}|{display.Bounds.Top}|{display.Bounds.Right}|{display.Bounds.Bottom}|" +
                $"{display.WorkArea.Left}|{display.WorkArea.Top}|{display.WorkArea.Right}|{display.WorkArea.Bottom}|" +
                $"{display.DpiX}|{display.DpiY}|{(display.IsPrimary ? 1 : 0)}\n");
        }

        return "explorer-topology-v1:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// 从 fresh Observation 推导当前拓扑身份；无法计算时返回 null。
    ///
    /// 写事实的一方与读事实的一方必须使用同一个推导口径，否则会出现"存得进、读不出"的假阴性：
    /// Adapter 校验事实与产品读取事实都走这里。
    /// </summary>
    public static string? TryComputeFor(DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var spacing = observation.PositioningCapabilities.Spacing;
        if (observation.Displays.Count == 0 || spacing.X <= 0 || spacing.Y <= 0)
            return null;

        // Compute 对不合法输入抛异常；读取路径先于 Observation 校验运行，因此这里按"无法计算"处理。
        if (observation.Displays.Any(static display =>
                string.IsNullOrWhiteSpace(display.DeviceName)))
        {
            return null;
        }

        return Compute(
            observation.PositioningCapabilities.IsAutoArrangeEnabled,
            spacing,
            observation.Displays);
    }
}
