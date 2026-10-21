namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// DesktopAgent 自己拥有的本地用户 Profile 边界。
/// 长期数据由 Store 管理，调用方只按需读取小工作集，不依赖模型供应商 Memory。
/// </summary>
public interface IDesktopUserProfileStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>同一 TemplateId + Category 只保留一个已确认偏好。</summary>
    Task SavePreferenceAsync(
        DesktopCategoryZonePreference preference,
        CancellationToken cancellationToken = default);

    Task<DesktopCategoryZonePreference?> GetPreferenceAsync(
        Guid templateId,
        string category,
        CancellationToken cancellationToken = default);

    Task<bool> DeletePreferenceAsync(
        Guid preferenceId,
        CancellationToken cancellationToken = default);

    /// <summary>追加一条不可变行为证据；EvidenceId 重复时拒绝，不做静默覆盖。</summary>
    Task AppendEvidenceAsync(
        DesktopClassificationBehaviorEvidence evidence,
        CancellationToken cancellationToken = default);

    /// <summary>只读取最近的有限证据窗口，newest first。</summary>
    Task<IReadOnlyList<DesktopClassificationBehaviorEvidence>> GetRecentEvidenceAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>隐私/重置入口：清空行为证据，但不删除用户已确认的 Rule / Preference。</summary>
    Task<int> ClearEvidenceAsync(CancellationToken cancellationToken = default);
}
