using System.Collections.ObjectModel;

namespace DesktopAgent.Core.Productization;

/// <summary>
/// Desktop Overlay 的逐 Zone 颜色覆盖。仅描述展示偏好，不进入布局几何、规则或执行合同。
/// 未配置覆盖的 Zone 继承 <see cref="DesktopOverlayAppearanceSettings.ZoneColorRgb"/>。
/// </summary>
public sealed class DesktopOverlayZoneColorPreferences
{
    private readonly IReadOnlyDictionary<Guid, uint> _overrides;

    public IReadOnlyDictionary<Guid, uint> Overrides => _overrides;

    public DesktopOverlayZoneColorPreferences(
        IEnumerable<KeyValuePair<Guid, uint>>? overrides = null)
    {
        var values = new Dictionary<Guid, uint>();
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                ValidateZoneId(pair.Key);
                ValidateColor(pair.Value);
                if (!values.TryAdd(pair.Key, pair.Value))
                    throw new ArgumentException($"ZoneId {pair.Key:D} 存在重复颜色覆盖。", nameof(overrides));
            }
        }

        _overrides = new ReadOnlyDictionary<Guid, uint>(values);
    }

    public bool TryGetOverride(Guid zoneId, out uint colorRgb)
    {
        ValidateZoneId(zoneId);
        return _overrides.TryGetValue(zoneId, out colorRgb);
    }

    public uint Resolve(Guid zoneId, uint inheritedColorRgb)
    {
        ValidateZoneId(zoneId);
        ValidateColor(inheritedColorRgb);
        return _overrides.TryGetValue(zoneId, out var colorRgb)
            ? colorRgb
            : inheritedColorRgb;
    }

    public DesktopOverlayZoneColorPreferences WithOverride(Guid zoneId, uint colorRgb)
    {
        ValidateZoneId(zoneId);
        ValidateColor(colorRgb);

        var values = _overrides.ToDictionary(pair => pair.Key, pair => pair.Value);
        values[zoneId] = colorRgb;
        return new DesktopOverlayZoneColorPreferences(values);
    }

    public DesktopOverlayZoneColorPreferences WithoutOverride(Guid zoneId)
    {
        ValidateZoneId(zoneId);

        if (!_overrides.ContainsKey(zoneId))
            return this;

        var values = _overrides.ToDictionary(pair => pair.Key, pair => pair.Value);
        values.Remove(zoneId);
        return new DesktopOverlayZoneColorPreferences(values);
    }

    private static void ValidateZoneId(Guid zoneId)
    {
        if (zoneId == Guid.Empty)
            throw new ArgumentException("ZoneId 不能为 Guid.Empty。", nameof(zoneId));
    }

    private static void ValidateColor(uint colorRgb)
    {
        if (colorRgb > DesktopOverlayAppearanceSettings.MaxColorRgb)
            throw new ArgumentOutOfRangeException(nameof(colorRgb), "颜色必须是 24-bit RGB。 ");
    }

    public static DesktopOverlayZoneColorPreferences Empty { get; } = new();
}
