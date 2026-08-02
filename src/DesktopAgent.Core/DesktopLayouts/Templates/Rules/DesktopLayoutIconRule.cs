namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 桌面图标用户规则 — 不可变 sealed class。
/// 使用 StableId 精确匹配图标。构造时验证全部不变量。
/// </summary>
public sealed class DesktopLayoutIconRule
{
    /// <summary>匹配图标的 StableId（精确，Ordinal，不 Trim）。</summary>
    public string StableId { get; }

    /// <summary>规则类型。</summary>
    public DesktopLayoutIconRuleKind Kind { get; }

    /// <summary>
    /// ZoneOverride 的目标 ZoneId。Exclude/Pin 时必须为 null。
    /// </summary>
    public Guid? TargetZoneId { get; }

    public DesktopLayoutIconRule(string stableId, DesktopLayoutIconRuleKind kind, Guid? targetZoneId)
    {
        if (stableId is null)
            throw new ArgumentNullException(nameof(stableId));
        if (stableId.Length == 0)
            throw new ArgumentException("StableId 不能为空字符串。", nameof(stableId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentException($"无效的 RuleKind：{(int)kind}。", nameof(kind));

        switch (kind)
        {
            case DesktopLayoutIconRuleKind.Exclude:
            case DesktopLayoutIconRuleKind.Pin:
                if (targetZoneId.HasValue)
                    throw new ArgumentException(
                        $"{kind} 规则的 TargetZoneId 必须为 null。", nameof(targetZoneId));
                break;
            case DesktopLayoutIconRuleKind.ZoneOverride:
                if (!targetZoneId.HasValue)
                    throw new ArgumentException(
                        "ZoneOverride 规则必须指定 TargetZoneId。", nameof(targetZoneId));
                if (targetZoneId.Value == Guid.Empty)
                    throw new ArgumentException(
                        "ZoneOverride 的 TargetZoneId 不能为 Guid.Empty。", nameof(targetZoneId));
                break;
        }

        StableId = stableId;
        Kind = kind;
        TargetZoneId = targetZoneId;
    }

    /// <summary>创建 Exclude 规则。</summary>
    public static DesktopLayoutIconRule ForExclude(string stableId)
        => new(stableId, DesktopLayoutIconRuleKind.Exclude, null);

    /// <summary>创建 Pin 规则。</summary>
    public static DesktopLayoutIconRule ForPin(string stableId)
        => new(stableId, DesktopLayoutIconRuleKind.Pin, null);

    /// <summary>创建 ZoneOverride 规则。</summary>
    public static DesktopLayoutIconRule ForZoneOverride(string stableId, Guid targetZoneId)
        => new(stableId, DesktopLayoutIconRuleKind.ZoneOverride, targetZoneId);
}
