namespace DesktopAgent.WinUI;

/// <summary>
/// WinUI 产品壳的数据路径 facade。
/// 仅在产品程序集内遮蔽 Persistence 的旧默认路径，让 Dev / Packaging Test / Stable
/// 使用各自独立的产品数据根目录；Diagnostics 继续保持原有工程数据路径语义。
/// </summary>
internal static class DesktopAgentDataPaths
{
    internal static string DefaultDatabasePath => TesselProductDataPaths.DatabasePath;
    internal static string DefaultSettingsPath => TesselProductDataPaths.SettingsPath;
    internal static string DefaultSecretsDirectory => TesselProductDataPaths.SecretStoreDirectory;
    internal static string DefaultRadarCachePath => TesselProductDataPaths.RadarCachePath;
    internal static string DefaultAiWorkspaceRecoveryPath => TesselProductDataPaths.AiWorkspaceRecoveryPath;
}
