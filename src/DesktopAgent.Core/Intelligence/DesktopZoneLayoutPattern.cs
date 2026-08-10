namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// 首次桌面理解阶段允许 AI 选择的有限逻辑布局模式。
/// 枚举表达布局意图，不包含像素坐标或任意归一化数字。
/// </summary>
public enum DesktopZoneLayoutPattern
{
    Grid = 0,
    Columns = 1,
    Rows = 2,
    PrimaryLeft = 3,
    PrimaryRight = 4,
    PrimaryTop = 5,
    PrimaryBottom = 6,
}
