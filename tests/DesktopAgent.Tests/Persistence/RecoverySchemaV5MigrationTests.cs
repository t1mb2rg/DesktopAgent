using DesktopAgent.Persistence.DesktopLayouts;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Persistence;

public sealed class RecoverySchemaV5MigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public RecoverySchemaV5MigrationTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(), $"DesktopAgent.Tests.RecoverySchemaV5.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // 尽力清理测试目录。
        }
    }

    [Fact]
    public async Task FreshDatabase_UsesCurrentSchemaWithoutLegacyRecoveryFlags()
    {
        var store = new SqliteRecoverySessionStore(_dbPath);
        await store.InitializeAsync();

        using var connection = OpenRawConnection();
        Assert.Equal(9, ReadUserVersion(connection));
        Assert.False(ColumnExists(connection, "desktop_layout_recovery_sessions", "requires_manual_recovery"));
        Assert.False(ColumnExists(connection, "desktop_layout_recovery_sessions", "is_position_uncertain"));
        Assert.True(ColumnExists(connection, "desktop_layout_recovery_sessions", "lifecycle"));
    }

    [Fact]
    public async Task V4Upgrade_DropsLegacyFlagsAndPreservesRecoveryEvidence()
    {
        var sessionId = "00000000-0000-0000-0000-000000000901";

        using (var connection = OpenRawConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $@"
                PRAGMA user_version = 4;

                CREATE TABLE desktop_layout_recovery_sessions (
                    session_id                  TEXT PRIMARY KEY NOT NULL,
                    safety_snapshot_id          TEXT NOT NULL,
                    target_device_name          TEXT NOT NULL,
                    started_at_utc              TEXT NOT NULL,
                    lifecycle                   INTEGER NOT NULL,
                    requires_manual_recovery    INTEGER NOT NULL,
                    is_position_uncertain       INTEGER NOT NULL,
                    last_failed_step_index      INTEGER NULL,
                    source_restore_plan_json    TEXT NOT NULL,
                    execution_plan_json         TEXT NOT NULL,
                    plan_payload_format_version INTEGER NOT NULL,
                    completed_forward_step_indexes_json         TEXT NOT NULL,
                    completed_rollback_source_step_indexes_json TEXT NOT NULL,
                    forward_failure_type        TEXT NULL,
                    forward_failure_message     TEXT NULL,
                    recovery_failure_type       TEXT NULL,
                    recovery_failure_message    TEXT NULL,
                    verification_result_json    TEXT NULL,
                    revision                    INTEGER NOT NULL,
                    updated_at_utc              TEXT NOT NULL,
                    forward_attempt_count       INTEGER NOT NULL,
                    rollback_attempt_count      INTEGER NOT NULL
                );

                CREATE TABLE desktop_layout_recovery_attempts (
                    session_id                  TEXT NOT NULL,
                    attempt_index               INTEGER NOT NULL,
                    source_step_index           INTEGER NOT NULL,
                    phase                       INTEGER NOT NULL,
                    expected_stable_id          TEXT NOT NULL,
                    expected_previous_x         INTEGER NULL,
                    expected_previous_y         INTEGER NULL,
                    requested_x                 INTEGER NOT NULL,
                    requested_y                 INTEGER NOT NULL,
                    reported_stable_id          TEXT NULL,
                    reported_previous_x         INTEGER NULL,
                    reported_previous_y         INTEGER NULL,
                    actual_x                    INTEGER NULL,
                    actual_y                    INTEGER NULL,
                    has_trusted_actual_position INTEGER NOT NULL,
                    is_exact                    INTEGER NOT NULL,
                    outcome                     INTEGER NOT NULL,
                    connection_method           INTEGER NULL,
                    exception_type              TEXT NULL,
                    error_message               TEXT NULL,
                    primary_path_exception_type TEXT NULL,
                    primary_path_exception_msg  TEXT NULL,
                    PRIMARY KEY (session_id, attempt_index),
                    FOREIGN KEY (session_id)
                        REFERENCES desktop_layout_recovery_sessions(session_id)
                        ON DELETE CASCADE
                );

                INSERT INTO desktop_layout_recovery_sessions VALUES (
                    '{sessionId}',
                    '00000000-0000-0000-0000-000000000902',
                    '\\.\DISPLAY1',
                    '2026-08-07T00:00:00.0000000+00:00',
                    5,
                    1,
                    1,
                    0,
                    '{{}}',
                    '{{}}',
                    1,
                    '[]',
                    '[]',
                    'WriterException',
                    'forward evidence',
                    'RecoveryObservationNotStable',
                    'recovery evidence',
                    NULL,
                    7,
                    '2026-08-07T00:01:00.0000000+00:00',
                    1,
                    0
                );

                INSERT INTO desktop_layout_recovery_attempts VALUES (
                    '{sessionId}', 0, 0, 0,
                    'desktop:item',
                    10, 20,
                    30, 40,
                    NULL, NULL, NULL,
                    NULL, NULL,
                    0, 0,
                    2,
                    NULL,
                    'WriterException',
                    'boom',
                    NULL,
                    NULL
                );
            ";
            command.ExecuteNonQuery();
        }

        var store = new SqliteRecoverySessionStore(_dbPath);
        await store.InitializeAsync();

        using var upgraded = OpenRawConnection();
        Assert.Equal(9, ReadUserVersion(upgraded));
        Assert.False(ColumnExists(upgraded, "desktop_layout_recovery_sessions", "requires_manual_recovery"));
        Assert.False(ColumnExists(upgraded, "desktop_layout_recovery_sessions", "is_position_uncertain"));

        using (var parent = upgraded.CreateCommand())
        {
            parent.CommandText = @"
                SELECT lifecycle, recovery_failure_type, revision,
                       forward_attempt_count, rollback_attempt_count
                FROM desktop_layout_recovery_sessions
                WHERE session_id = @sid;";
            parent.Parameters.AddWithValue("@sid", sessionId);
            using var reader = parent.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(5, reader.GetInt32(0));
            Assert.Equal("RecoveryObservationNotStable", reader.GetString(1));
            Assert.Equal(7, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(0, reader.GetInt32(4));
        }

        using (var child = upgraded.CreateCommand())
        {
            child.CommandText = @"
                SELECT expected_stable_id, exception_type, error_message
                FROM desktop_layout_recovery_attempts
                WHERE session_id = @sid AND attempt_index = 0;";
            child.Parameters.AddWithValue("@sid", sessionId);
            using var reader = child.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("desktop:item", reader.GetString(0));
            Assert.Equal("WriterException", reader.GetString(1));
            Assert.Equal("boom", reader.GetString(2));
        }
    }

    private SqliteConnection OpenRawConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
