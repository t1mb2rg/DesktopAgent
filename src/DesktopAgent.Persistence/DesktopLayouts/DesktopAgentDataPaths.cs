namespace DesktopAgent.Persistence.DesktopLayouts;

/// <summary>
/// DesktopAgent 默认本地数据路径。各 Store 仍允许调用方传入自定义路径以便测试/隔离。
/// </summary>
public static class DesktopAgentDataPaths
{
    /// <summary>
    /// 数据根目录的环境变量名。设置成非空绝对路径时，本类的默认路径（库、设置、密钥、雷达缓存）
    /// 全部落在这个目录下；用于把某个进程（例如验收用的 PoC）指向一份独立的库，
    /// 而不碰它平时用的 %LocalAppData%\DesktopAgent。
    /// 产品壳走的是按渠道解析的 TesselProductDataPaths，不受这个变量影响。
    /// </summary>
    public const string RootDirectoryEnvironmentVariable = "DESKTOPAGENT_DATA_DIR";

    /// <summary>默认根目录：环境变量非空则用它（含路径规范化），否则 %LocalAppData%\DesktopAgent。</summary>
    public static string ResolveRootDirectory(string? overrideDirectory) =>
        string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopAgent")
            : Path.GetFullPath(overrideDirectory);

    private static string DefaultRootDirectory =>
        ResolveRootDirectory(Environment.GetEnvironmentVariable(RootDirectoryEnvironmentVariable));

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
