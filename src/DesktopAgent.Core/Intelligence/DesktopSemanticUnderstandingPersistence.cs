namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// 一次可持久化的纯语义理解快照。
///
/// 它记录“当时基于哪次 Observation 得到了什么理解”，但不声明该理解在未来仍然 fresh。
/// Freshness 必须在运行时基于新的桌面事实重新判断，不能由数据库中的布尔值恢复。
/// </summary>
public sealed record DesktopSemanticUnderstandingSnapshot
{
    public Guid UnderstandingId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public Guid SourceObservationId { get; }
    public string SourceStateFingerprint { get; }
    public int SourceItemCount { get; }
    public DesktopSemanticUnderstanding Understanding { get; }

    public DesktopSemanticUnderstandingSnapshot(
        Guid understandingId,
        DateTimeOffset createdAtUtc,
        Guid sourceObservationId,
        string sourceStateFingerprint,
        int sourceItemCount,
        DesktopSemanticUnderstanding understanding)
    {
        if (understandingId == Guid.Empty)
            throw new ArgumentException("UnderstandingId 不能为 Guid.Empty。", nameof(understandingId));
        if (sourceObservationId == Guid.Empty)
            throw new ArgumentException("SourceObservationId 不能为 Guid.Empty。", nameof(sourceObservationId));
        if (string.IsNullOrWhiteSpace(sourceStateFingerprint))
            throw new ArgumentException("SourceStateFingerprint 不能为空。", nameof(sourceStateFingerprint));
        if (sourceItemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceItemCount), "SourceItemCount 必须大于 0。");
        ArgumentNullException.ThrowIfNull(understanding);
        if (understanding.Assignments.Count > sourceItemCount)
        {
            throw new ArgumentException(
                "Understanding 的 Assignment 数不能超过来源 Observation 的项目数。",
                nameof(sourceItemCount));
        }

        UnderstandingId = understandingId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        SourceObservationId = sourceObservationId;
        SourceStateFingerprint = sourceStateFingerprint.Trim();
        SourceItemCount = sourceItemCount;
        Understanding = understanding;
    }
}

/// <summary>
/// 纯语义理解快照 Store。
/// Store 只负责保存历史事实，不负责判断 Current / Stale，也不授予 Workspace 或 Writer authority。
/// </summary>
public interface IDesktopSemanticUnderstandingStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>追加一份不可变理解快照。重复 UnderstandingId 必须拒绝，而不是覆盖历史。</summary>
    Task AddAsync(
        DesktopSemanticUnderstandingSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<DesktopSemanticUnderstandingSnapshot?> GetAsync(
        Guid understandingId,
        CancellationToken cancellationToken = default);

    /// <summary>按 CreatedAtUtc DESC、UnderstandingId DESC 返回最近一份理解。</summary>
    Task<DesktopSemanticUnderstandingSnapshot?> GetLatestAsync(
        CancellationToken cancellationToken = default);
}
