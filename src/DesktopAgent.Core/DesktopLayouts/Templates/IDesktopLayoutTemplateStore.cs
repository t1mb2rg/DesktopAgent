namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// 桌面布局模板持久化存储。
/// </summary>
public interface IDesktopLayoutTemplateStore
{
    /// <summary>初始化数据库（幂等）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>保存模板。按 TemplateId 原子新增或完整替换。</summary>
    Task SaveAsync(DesktopLayoutTemplate template, CancellationToken cancellationToken = default);

    /// <summary>读取完整模板。不存在时返回 null。</summary>
    Task<DesktopLayoutTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default);

    /// <summary>列出模板摘要，按 Name BINARY ASC, TemplateId ASC。不加载完整 Zones。</summary>
    Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(
        int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>删除模板（级联删除 Zones）。返回是否实际删除。</summary>
    Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default);
}
