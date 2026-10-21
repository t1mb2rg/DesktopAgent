using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces;

/// <summary>显式白名单格式。只保存草稿、语义偏好、聊天与撤销历史，不接受执行数据。</summary>
public sealed class JsonDesktopWorkspaceSessionStore : IDesktopWorkspaceSessionStore
{
    private const int FormatVersion = 1;
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDesktopWorkspaceSessionStore(string path) => _path = Path.GetFullPath(path);

    public async Task<DesktopWorkspaceSessionSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(DesktopWorkspaceSessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        DesktopWorkspaceDesignSession.ValidateSnapshot(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToPayload(snapshot), Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("设计会话过大，未覆盖已保存记录。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.SessionId != snapshot.SessionId)
                    throw new InvalidOperationException("不能用另一会话覆盖现有设计记录。");
                if (existing.Sequence > snapshot.Sequence) return;
                if (existing.Sequence == snapshot.Sequence)
                {
                    if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(ToPayload(existing), Options)))
                        throw new InvalidDataException("相同版本的设计记录内容冲突。");
                    return;
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    private async Task<DesktopWorkspaceSessionSnapshot?> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > MaximumBytes) throw new InvalidDataException("设计会话文件超出大小限制。");
        var bytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("设计会话文件超出大小限制。");
        try
        {
            var dto = JsonSerializer.Deserialize<SessionDto>(bytes, Options) ?? throw new JsonException("空会话。");
            if (dto.FormatVersion != FormatVersion) throw new InvalidDataException("不支持的设计会话格式版本。");
            var snapshot = new DesktopWorkspaceSessionSnapshot(dto.SessionId, dto.Revision, dto.Sequence,
                FromState(dto.State), dto.UndoHistory.Select(FromState).ToArray(), dto.RedoHistory.Select(FromState).ToArray(),
                dto.Messages.Select(message => new DesktopWorkspaceChatMessage(message.MessageId, message.Role,
                    message.Text, message.CreatedAtUtc)).ToArray());
            DesktopWorkspaceDesignSession.ValidateSnapshot(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("设计会话损坏或内容无效，原文件已保留。", ex);
        }
    }

    private static SessionDto ToPayload(DesktopWorkspaceSessionSnapshot snapshot) => new(FormatVersion,
        snapshot.SessionId, snapshot.Revision, snapshot.Sequence, ToState(snapshot.State),
        snapshot.UndoHistory.Select(ToState).ToArray(), snapshot.RedoHistory.Select(ToState).ToArray(),
        snapshot.Messages.Select(message => new MessageDto(message.MessageId, message.Role, message.Text, message.CreatedAtUtc)).ToArray());

    private static StateDto ToState(DesktopWorkspaceEditState state)
    {
        var draft = state.Draft;
        var intent = state.Intent;
        return new(new(draft.DesignId, draft.Summary,
            draft.DisplayPolicies.Select(policy => new DisplayDto(policy.DisplayRef, policy.Policy)).ToArray(),
            draft.Zones.Select(zone => new ZoneDto(zone.ZoneKey, zone.Name, zone.DisplayRef, RectDto.From(zone.Bounds), zone.Anchor, zone.PackingOrder)).ToArray(),
            draft.ZoneRelations.Select(relation => new RelationDto(relation.ZoneKey, relation.Relation, relation.OtherZoneKey)).ToArray(),
            draft.CategoryRules.Select(rule => new CategoryDto(rule.CategoryKey, rule.ZoneKey)).ToArray(),
            draft.ItemOverrides.Select(item => new OverrideDto(item.ItemRef, item.Kind, item.ZoneKey, item.Anchor)).ToArray(),
            draft.ReservedRegions.Select(region => new ReservedDto(region.RegionKey, region.Purpose, region.DisplayRef, RectDto.From(region.Bounds))).ToArray()),
            new(intent.Summary, intent.CategoryDisplayTargets.ToDictionary(pair => pair.Key, pair => pair.Value),
                intent.ItemDisplayTargets.ToDictionary(pair => pair.Key, pair => pair.Value),
                intent.DisplayLoads.ToDictionary(pair => pair.Key, pair => pair.Value), intent.ReservedRegions.ToArray()));
    }

    private static DesktopWorkspaceEditState FromState(StateDto state)
    {
        var draft = state.Draft;
        var intent = state.Intent;
        return new(new(draft.DesignId, draft.Summary,
            draft.DisplayPolicies.Select(policy => new DesktopWorkspaceBlueprintDisplayPolicy(policy.DisplayRef, policy.Policy)),
            draft.Zones.Select(zone => new DesktopWorkspaceBlueprintZone(zone.ZoneKey, zone.Name, zone.DisplayRef,
                zone.Bounds.ToRect(), zone.Anchor, zone.PackingOrder)),
            draft.ZoneRelations.Select(relation => new DesktopWorkspaceBlueprintZoneRelation(relation.ZoneKey, relation.Relation, relation.OtherZoneKey)),
            draft.CategoryRules.Select(rule => new DesktopWorkspaceBlueprintCategoryRule(rule.CategoryKey, rule.ZoneKey)),
            draft.ItemOverrides.Select(item => new DesktopWorkspaceBlueprintItemOverride(item.ItemRef, item.Kind, item.ZoneKey, item.Anchor)),
            draft.ReservedRegions.Select(region => new DesktopWorkspaceBlueprintReservedRegion(region.RegionKey, region.Purpose, region.DisplayRef, region.Bounds.ToRect()))),
            new(intent.Summary, intent.CategoryDisplayTargets, intent.ItemDisplayTargets, intent.DisplayLoads, intent.ReservedRegions));
    }

    private sealed record SessionDto(int FormatVersion, Guid SessionId, long Revision, long Sequence, StateDto State,
        StateDto[] UndoHistory, StateDto[] RedoHistory, MessageDto[] Messages);
    private sealed record StateDto(DraftDto Draft, IntentDto Intent);
    private sealed record DraftDto(Guid DesignId, string Summary, DisplayDto[] DisplayPolicies, ZoneDto[] Zones,
        RelationDto[] ZoneRelations, CategoryDto[] CategoryRules, OverrideDto[] ItemOverrides, ReservedDto[] ReservedRegions);
    private sealed record IntentDto(string Summary, Dictionary<string, string> CategoryDisplayTargets,
        Dictionary<string, string> ItemDisplayTargets, Dictionary<string, DesktopWorkspaceDisplayLoad> DisplayLoads,
        DesktopWorkspaceReservedIntent[] ReservedRegions);
    private sealed record DisplayDto(string DisplayRef, DesktopWorkspaceDisplayPolicyKind Policy);
    private sealed record ZoneDto(string ZoneKey, string Name, string DisplayRef, RectDto Bounds,
        DesktopWorkspaceAnchorKind Anchor, DesktopWorkspacePackingOrder PackingOrder);
    private sealed record RelationDto(string ZoneKey, DesktopWorkspaceZoneRelationKind Relation, string OtherZoneKey);
    private sealed record CategoryDto(string CategoryKey, string ZoneKey);
    private sealed record OverrideDto(string ItemRef, DesktopWorkspaceItemOverrideKind Kind, string? ZoneKey, DesktopWorkspaceAnchorKind? Anchor);
    private sealed record ReservedDto(string RegionKey, string Purpose, string DisplayRef, RectDto Bounds);
    private sealed record MessageDto(Guid MessageId, DesktopWorkspaceMessageRole Role, string Text, DateTimeOffset CreatedAtUtc);
    private sealed record RectDto(double Left, double Top, double Width, double Height)
    {
        internal static RectDto From(DesktopWorkspaceRelativeRect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);
        internal DesktopWorkspaceRelativeRect ToRect() => new(Left, Top, Width, Height);
    }
}
