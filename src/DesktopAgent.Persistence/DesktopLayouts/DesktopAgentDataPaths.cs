namespace DesktopAgent.Persistence.DesktopLayouts;

/// <summary>
/// DesktopAgent 默认本地数据路径。各 Store 仍允许调用方传入自定义路径以便测试/隔离。
/// </summary>
public static class DesktopAgentDataPaths
{
    private static string DefaultRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopAgent");

    /// <summary>默认数据库路径：%LocalAppData%\DesktopAgent\desktop-agent.db</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(DefaultRootDirectory, "desktop-agent.db");

    /// <summary>产品 UI 偏好：%LocalAppData%\DesktopAgent\settings.json</summary>
    public static string DefaultSettingsPath =>
        Path.Combine(DefaultRootDirectory, "settings.json");

    /// <summary>DPAPI 保护后的 Secret blob 目录。</summary>
    public static string DefaultSecretsDirectory =>
        Path.Combine(DefaultRootDirectory, "secrets");

    /// <summary>
    /// Desktop Radar presentation-only cache。
    /// 删除该文件只会丢失 Overview 首帧缓存，不影响 Layout / Recovery / Writer 事实。
    /// </summary>
    public static string DefaultRadarCachePath =>
        Path.Combine(DefaultRootDirectory, "cache", "desktop-radar.json");
}
