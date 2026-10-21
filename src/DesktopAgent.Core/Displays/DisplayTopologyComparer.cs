namespace DesktopAgent.Core.Displays;

/// <summary>
/// 显示拓扑的确定性等价比较。
/// 列表顺序不具有语义；DeviceName 使用 Ordinal 精确匹配。
/// </summary>
public static class DisplayTopologyComparer
{
    public static bool AreEquivalent(
        IReadOnlyList<DisplayDescriptor> left,
        IReadOnlyList<DisplayDescriptor> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
            return false;

        var orderedLeft = left
            .OrderBy(display => display.DeviceName, StringComparer.Ordinal)
            .ToArray();
        var orderedRight = right
            .OrderBy(display => display.DeviceName, StringComparer.Ordinal)
            .ToArray();

        return orderedLeft.SequenceEqual(orderedRight);
    }
}
