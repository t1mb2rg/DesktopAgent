using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Database;

/// <summary>
/// 共享连接工厂。统一打开 SqliteConnection，保证所有 Store 使用一致的连接配置。
/// </summary>
internal static class SqliteDesktopAgentConnectionFactory
{
    /// <summary>
    /// 打开到 databasePath 的连接并返回已 Open 的 SqliteConnection。
    /// 设置：ReadWriteCreate、外键开启、连接池关闭。
    /// </summary>
    public static SqliteConnection Open(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException(
                "数据库路径不能为 null、空或纯空白。", nameof(databasePath));

        var absolutePath = Path.GetFullPath(databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}
