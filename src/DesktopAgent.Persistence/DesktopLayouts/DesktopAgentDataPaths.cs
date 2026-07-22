namespace DesktopAgent.Persistence.DesktopLayouts;

/// <summary>
/// 应用程序数据路径。SqliteDesktopLayoutSnapshotStore 本身不依赖此类，
/// 可以传入任意路径。仅当 PoC 需要默认路径时使用。
/// </summary>
public static class DesktopAgentDataPaths
{
    /// <summary>默认数据库路径：%LocalAppData%\DesktopAgent\desktop-agent.db</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopAgent",
            "desktop-agent.db");
}
