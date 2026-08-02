using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Preview;

/// <summary>
/// 根据 TargetDisplayBinding 和可用显示器列表解析目标显示器。
/// 不做静默回退。
/// </summary>
public static class DesktopLayoutTargetDisplayResolver
{
    public static DisplayDescriptor Resolve(
        TargetDisplayBinding binding,
        IReadOnlyList<DisplayDescriptor> displays,
        string? selectedTargetDeviceName)
    {
        if (binding is null) throw new ArgumentNullException(nameof(binding));
        if (displays is null) throw new ArgumentNullException(nameof(displays));

        // 验证 displays 元素
        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < displays.Count; i++)
        {
            if (displays[i] is null)
                throw new ArgumentException($"displays[{i}] 为 null。", nameof(displays));
            if (string.IsNullOrWhiteSpace(displays[i].DeviceName))
                throw new ArgumentException(
                    $"displays[{i}] DeviceName 不能为 null、空或空白。", nameof(displays));
            if (!seenNames.TryAdd(displays[i].DeviceName, i))
                throw new ArgumentException(
                    $"displays 中 DeviceName \"{displays[i].DeviceName}\" 重复。" +
                    $"首次出现在索引 {seenNames[displays[i].DeviceName]}，当前重复索引 {i}。", nameof(displays));
        }

        return binding.Kind switch
        {
            TargetDisplayBindingKind.SelectedTarget => ResolveSelectedTarget(
                selectedTargetDeviceName, displays),
            TargetDisplayBindingKind.PrimaryDisplay => ResolvePrimaryDisplay(displays),
            TargetDisplayBindingKind.ExactDeviceName => ResolveExactDeviceName(
                binding.DeviceName!, displays),
            _ => throw new ArgumentOutOfRangeException(nameof(binding),
                $"不支持的 TargetDisplayBindingKind: {binding.Kind}。")
        };
    }

    private static DisplayDescriptor ResolveSelectedTarget(
        string? selectedTargetDeviceName,
        IReadOnlyList<DisplayDescriptor> displays)
    {
        if (string.IsNullOrWhiteSpace(selectedTargetDeviceName))
            throw new ArgumentException(
                "SelectedTarget 模式下 selectedTargetDeviceName 不能为 null、空或空白。",
                nameof(selectedTargetDeviceName));

        foreach (var d in displays)
        {
            if (string.Equals(d.DeviceName, selectedTargetDeviceName, StringComparison.Ordinal))
                return d;
        }

        throw new InvalidOperationException(
            $"在 displays 中未找到与 selectedTargetDeviceName " +
            $"\"{selectedTargetDeviceName}\" (Ordinal) 匹配的显示器。");
    }

    private static DisplayDescriptor ResolvePrimaryDisplay(
        IReadOnlyList<DisplayDescriptor> displays)
    {
        DisplayDescriptor? found = null;
        foreach (var d in displays)
        {
            if (d.IsPrimary)
            {
                if (found is not null)
                    throw new InvalidOperationException(
                        "displays 中存在多个 IsPrimary == true 的显示器。");
                found = d;
            }
        }

        if (found is null)
            throw new InvalidOperationException(
                "displays 中不存在 IsPrimary == true 的显示器。");

        return found;
    }

    private static DisplayDescriptor ResolveExactDeviceName(
        string deviceName,
        IReadOnlyList<DisplayDescriptor> displays)
    {
        foreach (var d in displays)
        {
            if (string.Equals(d.DeviceName, deviceName, StringComparison.Ordinal))
                return d;
        }

        throw new InvalidOperationException(
            $"在 displays 中未找到与 ExactDeviceName " +
            $"\"{deviceName}\" (Ordinal) 匹配的显示器。");
    }
}
