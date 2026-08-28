using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Intelligence;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Persistence;

public sealed class SqliteDesktopSemanticUnderstandingStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopSemanticUnderstandingStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"DesktopAgent.Tests.SemanticUnderstandingStore.{Guid.NewGuid():N}");
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
    public async Task AddGetAndLatest_RoundTripPureSemanticUnderstanding_WithoutFreshnessAuthority()
    {
        var store = new SqliteDesktopSemanticUnderstandingStore(_dbPath);
        await store.InitializeAsync();

        Assert.Null(await store.GetLatestAsync());

        var snapshot = CreateSnapshot(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            new DateTimeOffset(2026, 8, 28, 12, 30, 0, TimeSpan.FromHours(8)),
            "state-fingerprint-001");

        await store.AddAsync(snapshot);

        var loaded = Assert.IsType<DesktopSemanticUnderstandingSnapshot>(
            await store.GetAsync(snapshot.UnderstandingId));
        var latest = Assert.IsType<DesktopSemanticUnderstandingSnapshot>(
            await store.GetLatestAsync());

        Assert.Equal(snapshot.UnderstandingId, loaded.UnderstandingId);
        Assert.Equal(snapshot.CreatedAtUtc, loaded.CreatedAtUtc);
        Assert.Equal(snapshot.SourceObservationId, loaded.SourceObservationId);
        Assert.Equal(snapshot.SourceStateFingerprint, loaded.SourceStateFingerprint);
        Assert.Equal(snapshot.SourceItemCount, loaded.SourceItemCount);
        Assert.Equal(snapshot.UnderstandingId, latest.UnderstandingId);

        Assert.Equal("桌面主要包含开发、学习和娱乐入口。", loaded.Understanding.DesktopSummary);
        Assert.Collection(
            loaded.Understanding.Categories,
            category =>
            {
                Assert.Equal("development", category.Key);
                Assert.Equal("开发", category.Name);
                Assert.Equal("开发工具和项目入口", category.Description);
            },
            category =>
            {
                Assert.Equal("study", category.Key);
                Assert.Equal("学习", category.Name);
                Assert.Equal("课程与资料", category.Description);
            });
        Assert.Collection(
            loaded.Understanding.Assignments,
            assignment =>
            {
                Assert.Equal("stable-vscode", assignment.StableId);
                Assert.Equal("development", assignment.CategoryKey);
                Assert.Equal(0.97, assignment.Confidence);
                Assert.Equal("IDE 快捷入口", assignment.Reason);
            },
            assignment =>
            {
                Assert.Equal("stable-report", assignment.StableId);
                Assert.Equal("study", assignment.CategoryKey);
                Assert.Equal(0.88, assignment.Confidence);
                Assert.Equal("课程报告", assignment.Reason);
            });
        Assert.Collection(
            loaded.Understanding.Questions,
            question =>
            {
                Assert.Equal("temporary-files", question.Key);
                Assert.Equal("临时文件希望归档还是保留？", question.Text);
            });

        var payload = ReadScalarString(
            "SELECT payload_json FROM desktop_semantic_understandings LIMIT 1;");
        Assert.Contains("desktop_summary", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("is_fresh", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is_current", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writer", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAsync_DuplicateUnderstandingId_IsRejectedWithoutOverwritingHistory()
    {
        var store = new SqliteDesktopSemanticUnderstandingStore(_dbPath);
        await store.InitializeAsync();

        var id = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var original = CreateSnapshot(
            id,
            new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero),
            "original-state");
        var replacement = CreateSnapshot(
            id,
            new DateTimeOffset(2026, 8, 28, 2, 0, 0, TimeSpan.Zero),
            "replacement-state");

        await store.AddAsync(original);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AddAsync(replacement));
        Assert.Contains("不可覆盖", ex.Message, StringComparison.Ordinal);

        var loaded = Assert.IsType<DesktopSemanticUnderstandingSnapshot>(
            await store.GetAsync(id));
        Assert.Equal("original-state", loaded.SourceStateFingerprint);
        Assert.Equal(original.CreatedAtUtc, loaded.CreatedAtUtc);
    }

    [Fact]
    public async Task GetLatestAsync_OrdersByCreatedAtThenUnderstandingIdDescending()
    {
        var store = new SqliteDesktopSemanticUnderstandingStore(_dbPath);
        await store.InitializeAsync();

        var sameTime = new DateTimeOffset(2026, 8, 28, 2, 0, 0, TimeSpan.Zero);
        var lowId = CreateSnapshot(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            sameTime,
            "low-id");
        var highId = CreateSnapshot(
            Guid.Parse("f0000000-0000-0000-0000-000000000001"),
            sameTime,
            "high-id");
        var older = CreateSnapshot(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            sameTime.AddMinutes(-1),
            "older");

        await store.AddAsync(highId);
        await store.AddAsync(older);
        await store.AddAsync(lowId);

        var latest = Assert.IsType<DesktopSemanticUnderstandingSnapshot>(
            await store.GetLatestAsync());
        Assert.Equal(highId.UnderstandingId, latest.UnderstandingId);
    }

    [Fact]
    public async Task GetAsync_UnknownPayloadVersionOrCorruptDomainPayload_FailsClosed()
    {
        var store = new SqliteDesktopSemanticUnderstandingStore(_dbPath);
        await store.InitializeAsync();

        var first = CreateSnapshot(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            DateTimeOffset.UtcNow,
            "bad-version");
        await store.AddAsync(first);

        ExecuteNonQuery(@"
            UPDATE desktop_semantic_understandings
            SET payload_format_version = 99
            WHERE understanding_id = '40000000-0000-0000-0000-000000000001';
        ");

        var versionEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAsync(first.UnderstandingId));
        Assert.Contains("payload_format_version=99", versionEx.Message, StringComparison.Ordinal);

        var second = CreateSnapshot(
            Guid.Parse("40000000-0000-0000-0000-000000000002"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            "bad-cross-reference");
        await store.AddAsync(second);

        ExecuteNonQuery(@"
            UPDATE desktop_semantic_understandings
            SET payload_json =
                '{""desktop_summary"":""bad"",""categories"":[{""key"":""known"",""name"":""Known"",""description"":""Known""}],""assignments"":[{""stable_id"":""item"",""category_key"":""missing"",""confidence"":0.9,""reason"":""bad""}],""questions"":[]}'
            WHERE understanding_id = '40000000-0000-0000-0000-000000000002';
        ");

        var semanticEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAsync(second.UnderstandingId));
        Assert.Contains("领域校验", semanticEx.Message, StringComparison.Ordinal);
    }

    private static DesktopSemanticUnderstandingSnapshot CreateSnapshot(
        Guid understandingId,
        DateTimeOffset createdAt,
        string fingerprint)
    {
        var understanding = new DesktopSemanticUnderstanding(
            "桌面主要包含开发、学习和娱乐入口。",
            [
                new DesktopSemanticCategory("development", "开发", "开发工具和项目入口"),
                new DesktopSemanticCategory("study", "学习", "课程与资料"),
            ],
            [
                new DesktopSemanticAssignment(
                    "stable-vscode", "development", 0.97, "IDE 快捷入口"),
                new DesktopSemanticAssignment(
                    "stable-report", "study", 0.88, "课程报告"),
            ],
            [
                new DesktopSemanticQuestion(
                    "temporary-files", "临时文件希望归档还是保留？"),
            ]);

        return new DesktopSemanticUnderstandingSnapshot(
            understandingId,
            createdAt,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            fingerprint,
            3,
            understanding);
    }

    private string ReadScalarString(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private void ExecuteNonQuery(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}
