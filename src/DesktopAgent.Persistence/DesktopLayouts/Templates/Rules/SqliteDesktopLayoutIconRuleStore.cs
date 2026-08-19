using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;

/// <summary>
/// SQLite 桌面图标规则存储实现。
/// 通过共享 SqliteDesktopAgentSchema.Initialize 确保 v3+ Schema。
/// </summary>
public sealed class SqliteDesktopLayoutIconRuleStore : IDesktopLayoutIconRuleStore
{
    private readonly string _databasePath;

    public SqliteDesktopLayoutIconRuleStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException(
                "数据库路径不能为 null、空或纯空白。", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    private SqliteConnection CreateConnection() =>
        SqliteDesktopAgentConnectionFactory.Open(_databasePath);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = Path.GetDirectoryName(_databasePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var connection = CreateConnection();
        SqliteDesktopAgentSchema.Initialize(connection, cancellationToken);
        return Task.CompletedTask;
    }

    // ═══════════════════════════ SaveAsync ═══════════════════════════

    public async Task SaveAsync(
        DesktopLayoutIconRuleSet ruleSet,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ruleSet is null)
            throw new ArgumentNullException(nameof(ruleSet));

        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();

        try
        {
            var templateIdStr = ruleSet.TemplateId.ToString("D");

            // 1. 验证目标模板存在
            if (!TemplateExists(connection, tx, ruleSet.TemplateId, templateIdStr))
                throw new ArgumentException(
                    $"TemplateId \"{templateIdStr}\" 的模板不存在，无法保存规则。",
                    nameof(ruleSet));

            // 2. 对每条 ZoneOverride，验证 TargetZone 存在且属于同一模板
            foreach (var rule in ruleSet.Rules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
                {
                    var zidStr = rule.TargetZoneId!.Value.ToString("D");
                    if (!ZoneExists(connection, tx, templateIdStr, zidStr))
                        throw new ArgumentException(
                            $"ZoneOverride 规则 (StableId=\"{rule.StableId}\") " +
                            $"TargetZoneId \"{zidStr}\" 不存在或不属于模板 \"{templateIdStr}\"。",
                            nameof(ruleSet));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 3. 先验证，再删除旧规则
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = @"
                    DELETE FROM desktop_layout_template_icon_rules
                    WHERE template_id = @tid;
                ";
                del.Parameters.AddWithValue("@tid", templateIdStr);
                del.ExecuteNonQuery();
            }

            // 4. 插入新规则（按 RuleSet.Rules 顺序，确定性）
            foreach (var rule in ruleSet.Rules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"
                    INSERT INTO desktop_layout_template_icon_rules
                        (template_id, stable_id, rule_kind, target_zone_id)
                    VALUES
                        (@tid, @sid, @kind, @zid);
                ";
                ins.Parameters.AddWithValue("@tid", templateIdStr);
                ins.Parameters.AddWithValue("@sid", rule.StableId); // 原样保存
                ins.Parameters.AddWithValue("@kind", (int)rule.Kind);
                ins.Parameters.AddWithValue("@zid",
                    rule.TargetZoneId.HasValue
                        ? (object)rule.TargetZoneId.Value.ToString("D")
                        : DBNull.Value);
                ins.ExecuteNonQuery();
            }

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
            await Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                    && ex is not ArgumentException
                                    && ex is not ArgumentNullException)
        {
            // SqliteException 等 → 事务自动回滚，旧规则完整保留
            throw;
        }
    }

    // ═══════════════════════════ GetAsync ═══════════════════════════

    public async Task<DesktopLayoutIconRuleSet?> GetAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (templateId == Guid.Empty)
            throw new ArgumentException(
                "TemplateId 不能为 Guid.Empty。", nameof(templateId));

        using var connection = CreateConnection();
        var templateIdStr = templateId.ToString("D");

        // 1. 检查模板是否存在
        if (!TemplateExists(connection, null, templateId, templateIdStr))
            return null;

        // 2. 查询规则
        var rules = ReadRules(connection, templateId, templateIdStr, cancellationToken);

        // 3. 构造 RuleSet
        try
        {
            return new DesktopLayoutIconRuleSet(templateId, rules);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"TemplateId \"{templateIdStr}\" 规则集合构造失败：{ex.Message}", ex);
        }
    }

    // ═══════════════════════════ DeleteAsync ═══════════════════════════

    public Task<bool> DeleteAsync(
        Guid templateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (templateId == Guid.Empty)
            throw new ArgumentException(
                "TemplateId 不能为 Guid.Empty。", nameof(templateId));

        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM desktop_layout_template_icon_rules
            WHERE template_id = @tid;
        ";
        cmd.Parameters.AddWithValue("@tid", templateId.ToString("D"));
        var rows = cmd.ExecuteNonQuery();
        return Task.FromResult(rows > 0);
    }

    // ═══════════════════════════ 内部辅助 ═══════════════════════════

    private static bool TemplateExists(
        SqliteConnection connection,
        SqliteTransaction? tx,
        Guid templateId,
        string templateIdStr)
    {
        using var cmd = connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT COUNT(*) FROM desktop_layout_templates
            WHERE template_id = @tid;
        ";
        cmd.Parameters.AddWithValue("@tid", templateIdStr);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static bool ZoneExists(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string templateIdStr,
        string zoneIdStr)
    {
        using var cmd = connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT COUNT(*) FROM desktop_layout_template_zones
            WHERE template_id = @tid AND zone_id = @zid;
        ";
        cmd.Parameters.AddWithValue("@tid", templateIdStr);
        cmd.Parameters.AddWithValue("@zid", zoneIdStr);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// 从数据库读取规则并恢复为领域对象。
    /// 所有损坏数据直接抛 InvalidDataException，不做静默修复。
    /// </summary>
    private static IReadOnlyList<DesktopLayoutIconRule> ReadRules(
        SqliteConnection connection,
        Guid templateId,
        string templateIdStr,
        CancellationToken cancellationToken)
    {
        var rules = new List<DesktopLayoutIconRule>();

        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT stable_id, rule_kind, target_zone_id
            FROM desktop_layout_template_icon_rules
            WHERE template_id = @tid
            ORDER BY stable_id COLLATE BINARY ASC;
        ";
        sel.Parameters.AddWithValue("@tid", templateIdStr);

        using var reader = sel.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stableId = reader.GetString(0); // 原样读取，不 Trim
            var kindVal = reader.GetInt32(1);
            var zoneIdStr = reader.IsDBNull(2) ? null : reader.GetString(2);

            // 验证 rule_kind 为已定义枚举值
            if (!Enum.IsDefined((DesktopLayoutIconRuleKind)kindVal))
                throw new InvalidDataException(
                    $"TemplateId \"{templateIdStr}\" StableId \"{stableId}\" " +
                    $"无效 rule_kind: {kindVal}。");

            var kind = (DesktopLayoutIconRuleKind)kindVal;

            // 解析 target_zone_id
            Guid? targetZoneId = null;
            if (zoneIdStr is not null)
            {
                if (!Guid.TryParseExact(zoneIdStr, "D", out var parsedZid))
                    throw new InvalidDataException(
                        $"TemplateId \"{templateIdStr}\" StableId \"{stableId}\" " +
                        $"target_zone_id \"{zoneIdStr}\" 不是有效 Guid。");
                targetZoneId = parsedZid;
            }

            // 验证 kind / target_zone_id 组合一致性
            switch (kind)
            {
                case DesktopLayoutIconRuleKind.Exclude:
                case DesktopLayoutIconRuleKind.Pin:
                    if (targetZoneId.HasValue)
                        throw new InvalidDataException(
                            $"TemplateId \"{templateIdStr}\" StableId \"{stableId}\" " +
                            $"{kind} 规则 target_zone_id 必须为 NULL，实际 \"{zoneIdStr}\"。");
                    break;

                case DesktopLayoutIconRuleKind.ZoneOverride:
                    if (!targetZoneId.HasValue)
                        throw new InvalidDataException(
                            $"TemplateId \"{templateIdStr}\" StableId \"{stableId}\" " +
                            "ZoneOverride 规则 target_zone_id 不能为 NULL。");
                    // 验证目标 Zone 存在且属于当前模板
                    if (!ZoneExists(connection, null, templateIdStr,
                            targetZoneId.Value.ToString("D")))
                        throw new InvalidDataException(
                            $"TemplateId \"{templateIdStr}\" StableId \"{stableId}\" " +
                            $"ZoneOverride TargetZoneId \"{zoneIdStr}\" " +
                            "不存在或不属于当前模板。");
                    break;

                default:
                    throw new InvalidDataException(
                        $"TemplateId \"{templateIdStr}\" StableId \"{stableId}\" " +
                        $"未处理的 rule_kind: {kindVal}。");
            }

            // 构造领域对象
            try
            {
                rules.Add(new DesktopLayoutIconRule(stableId, kind, targetZoneId));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(
                    $"TemplateId \"{templateIdStr}\" StableId \"{stableId}\" " +
                    $"领域对象构造失败：{ex.Message}", ex);
            }
        }

        return rules;
    }
}
