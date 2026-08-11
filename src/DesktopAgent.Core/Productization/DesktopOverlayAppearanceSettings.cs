namespace DesktopAgent.Core.Productization;

public enum DesktopOverlayAppearancePreset
{
    Light = 0,
    Standard = 1,
    Strong = 2,
    Custom = 3,
}

/// <summary>
/// Desktop Zone Overlay 的纯视觉偏好。它只描述展示，不包含布局、图标位置或执行权限。
/// </summary>
public sealed class DesktopOverlayAppearanceSettings
{
    public const double MinBorderThicknessDip = 1d;
    public const double MaxBorderThicknessDip = 8d;
    public const double MinBorderOpacity = 0.10d;
    public const double MaxBorderOpacity = 1d;
    public const double MinFillOpacity = 0d;
    public const double MaxFillOpacity = 0.35d;
    public const double MinCornerRadiusDip = 0d;
    public const double MaxCornerRadiusDip = 32d;

    public DesktopOverlayAppearancePreset Preset { get; }
    public double BorderThicknessDip { get; }
    public double BorderOpacity { get; }
    public double FillOpacity { get; }
    public double CornerRadiusDip { get; }

    public DesktopOverlayAppearanceSettings(
        DesktopOverlayAppearancePreset preset,
        double borderThicknessDip,
        double borderOpacity,
        double fillOpacity,
        double cornerRadiusDip)
    {
        if (!Enum.IsDefined(preset))
            throw new ArgumentOutOfRangeException(nameof(preset));
        if (!double.IsFinite(borderThicknessDip) ||
            borderThicknessDip is < MinBorderThicknessDip or > MaxBorderThicknessDip)
        {
            throw new ArgumentOutOfRangeException(nameof(borderThicknessDip));
        }
        if (!double.IsFinite(borderOpacity) ||
            borderOpacity is < MinBorderOpacity or > MaxBorderOpacity)
        {
            throw new ArgumentOutOfRangeException(nameof(borderOpacity));
        }
        if (!double.IsFinite(fillOpacity) ||
            fillOpacity is < MinFillOpacity or > MaxFillOpacity)
        {
            throw new ArgumentOutOfRangeException(nameof(fillOpacity));
        }
        if (!double.IsFinite(cornerRadiusDip) ||
            cornerRadiusDip is < MinCornerRadiusDip or > MaxCornerRadiusDip)
        {
            throw new ArgumentOutOfRangeException(nameof(cornerRadiusDip));
        }

        Preset = preset;
        BorderThicknessDip = borderThicknessDip;
        BorderOpacity = borderOpacity;
        FillOpacity = fillOpacity;
        CornerRadiusDip = cornerRadiusDip;
    }

    public DesktopOverlayAppearanceSettings WithCustomValues(
        double borderThicknessDip,
        double borderOpacity,
        double fillOpacity,
        double cornerRadiusDip)
        => new(
            DesktopOverlayAppearancePreset.Custom,
            borderThicknessDip,
            borderOpacity,
            fillOpacity,
            cornerRadiusDip);

    public static DesktopOverlayAppearanceSettings FromPreset(DesktopOverlayAppearancePreset preset)
        => preset switch
        {
            DesktopOverlayAppearancePreset.Light => new(
                preset,
                borderThicknessDip: 2d,
                borderOpacity: 0.34d,
                fillOpacity: 0.035d,
                cornerRadiusDip: 14d),
            DesktopOverlayAppearancePreset.Standard => new(
                preset,
                borderThicknessDip: 3d,
                borderOpacity: 0.52d,
                fillOpacity: 0.075d,
                cornerRadiusDip: 16d),
            DesktopOverlayAppearancePreset.Strong => new(
                preset,
                borderThicknessDip: 4d,
                borderOpacity: 0.74d,
                fillOpacity: 0.14d,
                cornerRadiusDip: 18d),
            DesktopOverlayAppearancePreset.Custom => throw new ArgumentException(
                "Custom 预设没有固定值，应显式提供高级参数。",
                nameof(preset)),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

    public static DesktopOverlayAppearanceSettings Standard { get; } =
        FromPreset(DesktopOverlayAppearancePreset.Standard);
}
