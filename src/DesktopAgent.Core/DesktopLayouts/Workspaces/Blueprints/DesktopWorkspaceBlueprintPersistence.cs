namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

/// <summary>Workspace Library 中一个 Blueprint 资产的轻量摘要。</summary>
public sealed record DesktopWorkspaceBlueprintAssetSummary
{
    public Guid BlueprintId { get; }
    public string Name { get; }
    public int CurrentRevision { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public bool IsArchived { get; }

    public DesktopWorkspaceBlueprintAssetSummary(
        Guid blueprintId,
        string name,
        int currentRevision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        bool isArchived)
    {
        if (blueprintId == Guid.Empty)
            throw new ArgumentException("BlueprintId 不能为 Guid.Empty。", nameof(blueprintId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        if (currentRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentRevision));

        var created = createdAtUtc.ToUniversalTime();
        var updated = updatedAtUtc.ToUniversalTime();
        if (updated < created)
            throw new ArgumentException("UpdatedAtUtc 不能早于 CreatedAtUtc。", nameof(updatedAtUtc));

        BlueprintId = blueprintId;
        Name = name.Trim();
        CurrentRevision = currentRevision;
        CreatedAtUtc = created;
        UpdatedAtUtc = updated;
        IsArchived = isArchived;
    }
}

/// <summary>
/// 一个不可变 Blueprint revision 连同其持久化来源信息。
/// SourceUserIntent 是当时的设计来源摘要，不代表未来任何执行授权。
/// </summary>
public sealed record DesktopWorkspaceBlueprintRevisionRecord
{
    public DesktopWorkspaceBlueprint Blueprint { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public Guid? SourceUnderstandingId { get; }
    public string? SourceUserIntent { get; }

    public DesktopWorkspaceBlueprintRevisionRecord(
        DesktopWorkspaceBlueprint blueprint,
        DateTimeOffset createdAtUtc,
        Guid? sourceUnderstandingId = null,
        string? sourceUserIntent = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        if (sourceUnderstandingId == Guid.Empty)
            throw new ArgumentException("SourceUnderstandingId 不能为 Guid.Empty。", nameof(sourceUnderstandingId));
        if (sourceUserIntent is not null && string.IsNullOrWhiteSpace(sourceUserIntent))
            throw new ArgumentException("SourceUserIntent 应使用 null 表示不存在，不能是空白。", nameof(sourceUserIntent));

        Blueprint = blueprint;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        SourceUnderstandingId = sourceUnderstandingId;
        SourceUserIntent = sourceUserIntent?.Trim();
    }
}

/// <summary>
/// 多显示器 Workspace Blueprint 的 durable store。
/// Blueprint revision 一旦写入必须不可变；Exact Plan、Frozen Preview、Confirmation 与 Writer authority
/// 均不属于该 Store。
/// </summary>
public interface IDesktopWorkspaceBlueprintStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新的 Blueprint 资产。FirstRevision 必须是 Revision=1 且 ParentRevision=null。
    /// 已存在相同 BlueprintId 时必须拒绝。
    /// </summary>
    Task CreateAsync(
        string name,
        DesktopWorkspaceBlueprintRevisionRecord firstRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 追加新的 immutable revision，并把资产的 CurrentRevision 推进到该 revision。
    /// 不能覆盖既有 revision，也不能倒退 CurrentRevision。
    /// </summary>
    Task AppendRevisionAsync(
        DesktopWorkspaceBlueprintRevisionRecord revision,
        CancellationToken cancellationToken = default);

    Task<DesktopWorkspaceBlueprintAssetSummary?> GetAssetAsync(
        Guid blueprintId,
        CancellationToken cancellationToken = default);

    Task<DesktopWorkspaceBlueprintRevisionRecord?> GetRevisionAsync(
        Guid blueprintId,
        int revision,
        CancellationToken cancellationToken = default);

    Task<DesktopWorkspaceBlueprintRevisionRecord?> GetCurrentRevisionAsync(
        Guid blueprintId,
        CancellationToken cancellationToken = default);

    /// <summary>列出 Workspace Library 资产，不加载 revision payload。</summary>
    Task<IReadOnlyList<DesktopWorkspaceBlueprintAssetSummary>> ListAsync(
        bool includeArchived = false,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把设计从设计库移除（归档）。只翻转资产的 <c>is_archived</c>，不删 immutable revisions，
    /// 因此历史版本仍可审计，也允许将来恢复；不触碰桌面图标，也不影响执行记录。
    /// 返回 false 表示该资产不存在或已被归档。
    /// </summary>
    Task<bool> ArchiveAsync(
        Guid blueprintId,
        CancellationToken cancellationToken = default);
}
