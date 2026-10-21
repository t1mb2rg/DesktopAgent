using DesktopAgent.Core.Automation;
using DesktopAgent.Persistence.Automation;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.Productization;

namespace DesktopAgent.WinUI;

internal sealed record OverviewLightweightReadModel(
    DesktopAutomationMode AutomationMode,
    Guid? ActiveLayoutTemplateId,
    string? ActiveLayoutName,
    int? ActiveLayoutZoneCount,
    int PendingCount)
{
    public string ActiveLayoutTitle => ActiveLayoutTemplateId switch
    {
        null => "尚未选择",
        _ when ActiveLayoutName is null => "布局不可用",
        _ => ActiveLayoutName,
    };

    public string ActiveLayoutDetail => ActiveLayoutTemplateId switch
    {
        null => "创建或选择一个布局后会显示在这里。",
        _ when ActiveLayoutName is null => "当前偏好指向的布局已经不存在。",
        _ => $"{ActiveLayoutZoneCount.GetValueOrDefault()} 个分区",
    };

    public string PendingTitle => PendingCount.ToString();

    public string PendingDetail => PendingCount == 0
        ? "目前没有需要你决定的项目。"
        : "需要你确认的项目会留在待确认队列。";
}

/// <summary>
/// Overview 的轻量只读入口。
/// 只读取产品设置、当前布局摘要来源与 Pending 数量；不读取 Shell、DesktopObservation、AI 或 Writer。
/// </summary>
internal sealed class OverviewLightweightReadModelLoader
{
    private readonly JsonDesktopProductSettingsStore _settingsStore =
        new(DesktopAgentDataPaths.DefaultSettingsPath);
    private readonly SqliteDesktopLayoutTemplateStore _layoutStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly SqliteDesktopPendingActionStore _pendingStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);

    private bool _initialized;

    public async Task<OverviewLightweightReadModel> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            await _settingsStore.InitializeAsync(cancellationToken);
            await _layoutStore.InitializeAsync(cancellationToken);
            await _pendingStore.InitializeAsync(cancellationToken);
            _initialized = true;
        }

        var settings = await _settingsStore.GetAsync(cancellationToken);

        string? activeLayoutName = null;
        int? activeLayoutZoneCount = null;
        if (settings.ActiveLayoutTemplateId is Guid activeLayoutTemplateId)
        {
            var template = await _layoutStore.GetAsync(activeLayoutTemplateId, cancellationToken);
            if (template is not null)
            {
                activeLayoutName = template.Name;
                activeLayoutZoneCount = template.Zones.Count;
            }
        }

        var pendingCount = await _pendingStore.CountAsync(cancellationToken);

        return new OverviewLightweightReadModel(
            settings.AutomationMode,
            settings.ActiveLayoutTemplateId,
            activeLayoutName,
            activeLayoutZoneCount,
            pendingCount);
    }
}
