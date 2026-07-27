using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts.Templates;

public sealed class SqliteDesktopLayoutTemplateStore : IDesktopLayoutTemplateStore
{
    private readonly string _databasePath;

    public SqliteDesktopLayoutTemplateStore(string databasePath)
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
        DesktopLayoutTemplate template, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (template is null)
            throw new ArgumentNullException(nameof(template));

        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();

        try
        {
            // 1. Upsert 父记录
            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = @"
                    INSERT INTO desktop_layout_templates
                        (template_id, name, version,
                         target_display_binding_kind, target_display_device_name, zone_count)
                    VALUES
                        (@id, @name, @version, @kind, @dev, @zoneCount)
                    ON CONFLICT(template_id) DO UPDATE SET
                        name = excluded.name,
                        version = excluded.version,
                        target_display_binding_kind = excluded.target_display_binding_kind,
                        target_display_device_name = excluded.target_display_device_name,
                        zone_count = excluded.zone_count;
                ";
                upsert.Parameters.AddWithValue("@id", template.TemplateId.ToString("D"));
                upsert.Parameters.AddWithValue("@name", template.Name);
                upsert.Parameters.AddWithValue("@version", template.Version);
                upsert.Parameters.AddWithValue("@kind", (int)template.TargetDisplayBinding.Kind);
                upsert.Parameters.AddWithValue("@dev",
                    (object?)template.TargetDisplayBinding.DeviceName ?? DBNull.Value);
                upsert.Parameters.AddWithValue("@zoneCount", template.Zones.Count);
                upsert.ExecuteNonQuery();
            }

            // 2. 删除旧 Zones
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = @"
                    DELETE FROM desktop_layout_template_zones
                    WHERE template_id = @id;
                ";
                del.Parameters.AddWithValue("@id", template.TemplateId.ToString("D"));
                del.ExecuteNonQuery();
            }

            // 3. 插入新 Zones（按输入顺序，ordinal = 0..N-1）
            for (var i = 0; i < template.Zones.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zone = template.Zones[i];

                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"
                    INSERT INTO desktop_layout_template_zones
                        (template_id, ordinal, zone_id, name,
                         left_value, top_value, right_value, bottom_value,
                         sort_order, placement_flow, max_item_count)
                    VALUES
                        (@tid, @ord, @zid, @name,
                         @left, @top, @right, @bottom,
                         @sort, @flow, @max);
                ";
                ins.Parameters.AddWithValue("@tid", template.TemplateId.ToString("D"));
                ins.Parameters.AddWithValue("@ord", i);
                ins.Parameters.AddWithValue("@zid", zone.ZoneId.ToString("D"));
                ins.Parameters.AddWithValue("@name", zone.Name);
                ins.Parameters.AddWithValue("@left", zone.Bounds.Left);
                ins.Parameters.AddWithValue("@top", zone.Bounds.Top);
                ins.Parameters.AddWithValue("@right", zone.Bounds.Right);
                ins.Parameters.AddWithValue("@bottom", zone.Bounds.Bottom);
                ins.Parameters.AddWithValue("@sort", zone.SortOrder);
                ins.Parameters.AddWithValue("@flow", (int)zone.PlacementFlow);
                ins.Parameters.AddWithValue("@max",
                    zone.MaxItemCount.HasValue ? (object)zone.MaxItemCount.Value : DBNull.Value);
                ins.ExecuteNonQuery();
            }

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
            await Task.CompletedTask;
        }
        catch
        {
            // 事务自动回滚
            throw;
        }
    }

    // ═══════════════════════════ GetAsync ═══════════════════════════

    public async Task<DesktopLayoutTemplate?> GetAsync(
        Guid templateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));

        using var connection = CreateConnection();

        // 读取父记录
        string name;
        int version;
        TargetDisplayBinding binding;
        int declaredZoneCount;

        using (var sel = connection.CreateCommand())
        {
            sel.CommandText = @"
                SELECT name, version,
                       target_display_binding_kind, target_display_device_name, zone_count
                FROM desktop_layout_templates
                WHERE template_id = @id
            ";
            sel.Parameters.AddWithValue("@id", templateId.ToString("D"));

            using var reader = sel.ExecuteReader();
            if (!reader.Read())
                return null;

            name = reader.GetString(0);
            version = reader.GetInt32(1);
            var kindVal = reader.GetInt32(2);
            var deviceName = reader.IsDBNull(3) ? null : reader.GetString(3);
            declaredZoneCount = reader.GetInt32(4);

            binding = RestoreBinding(kindVal, deviceName, templateId);
        }

        // 读取 Zones
        var zones = ReadZones(connection, templateId);

        if (zones.Count != declaredZoneCount)
            throw new InvalidDataException(
                $"TemplateId \"{templateId:D}\" 数据不一致：父表声明 {declaredZoneCount} 个分区，实际读取 {zones.Count} 个分区。");

        try
        {
            return new DesktopLayoutTemplate(
                templateId, name, version, binding, zones);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"TemplateId \"{templateId:D}\" 领域模型构造失败：{ex.Message}", ex);
        }
    }

    private static List<DesktopLayoutZone> ReadZones(
        SqliteConnection connection, Guid templateId)
    {
        var zones = new List<DesktopLayoutZone>();
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT zone_id, name, left_value, top_value, right_value, bottom_value,
                   sort_order, placement_flow, max_item_count
            FROM desktop_layout_template_zones
            WHERE template_id = @id
            ORDER BY ordinal ASC
        ";
        sel.Parameters.AddWithValue("@id", templateId.ToString("D"));

        using var reader = sel.ExecuteReader();
        while (reader.Read())
        {
            var zoneIdStr = reader.GetString(0);
            if (!Guid.TryParseExact(zoneIdStr, "D", out var zoneId))
                throw new InvalidDataException(
                    $"TemplateId \"{templateId:D}\" ZoneId \"{zoneIdStr}\" 不是有效 Guid。");

            var zoneName = reader.GetString(1);
            var left = reader.GetInt32(2);
            var top = reader.GetInt32(3);
            var right = reader.GetInt32(4);
            var bottom = reader.GetInt32(5);
            var sortOrder = reader.GetInt32(6);
            var flowVal = reader.GetInt32(7);
            int? maxItemCount = reader.IsDBNull(8) ? null : reader.GetInt32(8);

            if (!Enum.IsDefined((ZonePlacementFlow)flowVal))
                throw new InvalidDataException(
                    $"TemplateId \"{templateId:D}\" Zone \"{zoneIdStr}\" 无效 PlacementFlow 值: {flowVal}");

            try
            {
                var bounds = new NormalizedRect(left, top, right, bottom);
                zones.Add(new DesktopLayoutZone(
                    zoneId, zoneName, bounds, sortOrder,
                    (ZonePlacementFlow)flowVal, maxItemCount));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(
                    $"TemplateId \"{templateId:D}\" Zone \"{zoneIdStr}\" 构造失败：{ex.Message}", ex);
            }
        }

        return zones;
    }

    private static TargetDisplayBinding RestoreBinding(
        int kindVal, string? deviceName, Guid templateId)
    {
        if (!Enum.IsDefined((TargetDisplayBindingKind)kindVal))
            throw new InvalidDataException(
                $"TemplateId \"{templateId:D}\" 无效 TargetDisplayBindingKind 值: {kindVal}");

        var kind = (TargetDisplayBindingKind)kindVal;

        switch (kind)
        {
            case TargetDisplayBindingKind.SelectedTarget:
                if (deviceName is not null)
                    throw new InvalidDataException(
                        $"TemplateId \"{templateId:D}\" Binding 非法组合：" +
                        $"Kind={kind}({kindVal}) 要求 DeviceName=null，实际值为 \"{deviceName}\"。");
                return TargetDisplayBinding.ForSelectedTarget();

            case TargetDisplayBindingKind.PrimaryDisplay:
                if (deviceName is not null)
                    throw new InvalidDataException(
                        $"TemplateId \"{templateId:D}\" Binding 非法组合：" +
                        $"Kind={kind}({kindVal}) 要求 DeviceName=null，实际值为 \"{deviceName}\"。");
                return TargetDisplayBinding.ForPrimaryDisplay();

            case TargetDisplayBindingKind.ExactDeviceName:
                if (deviceName is null)
                    throw new InvalidDataException(
                        $"TemplateId \"{templateId:D}\" Binding 非法组合：" +
                        $"Kind=ExactDeviceName({kindVal}) 要求 DeviceName 非 null。");
                if (string.IsNullOrWhiteSpace(deviceName))
                    throw new InvalidDataException(
                        $"TemplateId \"{templateId:D}\" Binding 非法组合：" +
                        $"Kind=ExactDeviceName({kindVal}) DeviceName 不能为空白，实际值 \"{deviceName}\"。");
                try
                {
                    return TargetDisplayBinding.ForExactDeviceName(deviceName);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException(
                        $"TemplateId \"{templateId:D}\" Binding 恢复失败：{ex.Message}", ex);
                }

            default:
                throw new InvalidDataException(
                    $"TemplateId \"{templateId:D}\" 未处理的 TargetDisplayBindingKind: {kindVal}");
        }
    }

    // ═══════════════════════════ ListAsync ═══════════════════════════

    public async Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(
        int limit = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit, "limit 必须大于 0。");

        using var connection = CreateConnection();
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT template_id, name, version,
                   target_display_binding_kind, target_display_device_name, zone_count
            FROM desktop_layout_templates
            ORDER BY name COLLATE BINARY ASC, template_id ASC
            LIMIT @limit
        ";
        sel.Parameters.AddWithValue("@limit", limit);

        var results = new List<DesktopLayoutTemplateSummary>();
        using var reader = sel.ExecuteReader();
        while (reader.Read())
        {
            var idStr = reader.GetString(0);
            if (!Guid.TryParseExact(idStr, "D", out var templateId))
                throw new InvalidDataException(
                    $"TemplateId \"{idStr}\" 不是有效 Guid。");

            var name = reader.GetString(1);
            var version = reader.GetInt32(2);
            var kindVal = reader.GetInt32(3);
            var deviceName = reader.IsDBNull(4) ? null : reader.GetString(4);
            var zoneCount = reader.GetInt32(5);

            var binding = RestoreBinding(kindVal, deviceName, templateId);

            try
            {
                results.Add(new DesktopLayoutTemplateSummary(
                    templateId, name, version, binding, zoneCount));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(
                    $"TemplateId \"{templateId:D}\" 摘要构造失败：{ex.Message}", ex);
            }
        }

        return results;
    }

    // ═══════════════════════════ DeleteAsync ═══════════════════════════

    public async Task<bool> DeleteAsync(
        Guid templateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (templateId == Guid.Empty)
            throw new ArgumentException(
                "TemplateId 不能为 Guid.Empty。", nameof(templateId));

        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM desktop_layout_templates
            WHERE template_id = @id
        ";
        cmd.Parameters.AddWithValue("@id", templateId.ToString("D"));
        var rows = cmd.ExecuteNonQuery();
        return rows > 0;
    }
}
