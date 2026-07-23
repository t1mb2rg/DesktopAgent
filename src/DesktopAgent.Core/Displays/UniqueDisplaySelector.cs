namespace DesktopAgent.Core.Displays;

/// <summary>
/// 从显示器列表中按 DeviceName 唯一匹配一个 DisplayDescriptor。
/// 纯逻辑，不访问 Shell COM、WPF、SQLite。
/// </summary>
public static class UniqueDisplaySelector
{
    /// <summary>
    /// 使用 Ordinal 精确匹配查找唯一显示器。
    /// 0 个匹配或 >1 个匹配均抛出 InvalidOperationException。
    /// </summary>
    public static DisplayDescriptor FindByDeviceName(
        IReadOnlyList<DisplayDescriptor> displays,
        string deviceName)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (deviceName is null)
            throw new ArgumentNullException(nameof(deviceName));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("deviceName 不能为空白。", nameof(deviceName));

        DisplayDescriptor? match = null;
        var matchCount = 0;
        for (var i = 0; i < displays.Count; i++)
        {
            if (string.Equals(displays[i].DeviceName, deviceName, StringComparison.Ordinal))
            {
                matchCount++;
                match = displays[i];
            }
        }

        if (matchCount == 0)
            throw new InvalidOperationException(
                $"在显示器列表中未找到 DeviceName \"{deviceName}\"（0 个匹配）。");
        if (matchCount > 1)
            throw new InvalidOperationException(
                $"DeviceName \"{deviceName}\" 在显示器列表中有 {matchCount} 个匹配（预期恰好 1 个）。");

        return match!;
    }
}
