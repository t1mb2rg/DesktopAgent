namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 提供给智能 Provider 的可选逻辑 Zone。
/// 只暴露稳定 ZoneId 和用户可理解名称，不暴露最终像素坐标。
/// </summary>
public sealed class IntelligenceZoneOption
{
    public Guid ZoneId { get; }
    public string Name { get; }

    public IntelligenceZoneOption(Guid zoneId, string name)
    {
        if (zoneId == Guid.Empty)
            throw new ArgumentException("ZoneId 不能为 Guid.Empty。", nameof(zoneId));
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为 null、空字符串或空白。", nameof(name));

        ZoneId = zoneId;
        Name = name;
    }
}
