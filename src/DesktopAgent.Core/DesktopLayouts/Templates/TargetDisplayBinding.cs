namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// 目标显示器绑定。枚举三种模式，每种子类构造均通过静态工厂。
/// 不允许公开构造非法组合（例如 SelectedTarget 带 DeviceName）。
/// </summary>
public sealed record TargetDisplayBinding
{
    public TargetDisplayBindingKind Kind { get; }
    public string? DeviceName { get; }

    private TargetDisplayBinding(TargetDisplayBindingKind kind, string? deviceName)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind,
                $"TargetDisplayBindingKind 值 {kind} 未定义。");

        Kind = kind;
        DeviceName = deviceName;
    }

    /// <summary>绑定到用户在 WPF 中选择的目标显示器。</summary>
    public static TargetDisplayBinding ForSelectedTarget() =>
        new(TargetDisplayBindingKind.SelectedTarget, null);

    /// <summary>绑定到主显示器。</summary>
    public static TargetDisplayBinding ForPrimaryDisplay() =>
        new(TargetDisplayBindingKind.PrimaryDisplay, null);

    /// <summary>
    /// 绑定到固定设备名。名称保留原样，不做 Trim 或大小写转换。
    /// 身份语义为 StringComparison.Ordinal。
    /// </summary>
    public static TargetDisplayBinding ForExactDeviceName(string deviceName)
    {
        if (deviceName is null)
            throw new ArgumentNullException(nameof(deviceName));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException(
                "ExactDeviceName 的 deviceName 不能为 null、空字符串或空白。",
                nameof(deviceName));

        return new TargetDisplayBinding(TargetDisplayBindingKind.ExactDeviceName, deviceName);
    }
}
