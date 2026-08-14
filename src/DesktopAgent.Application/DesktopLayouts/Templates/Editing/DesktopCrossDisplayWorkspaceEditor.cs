using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 用户显式发起的一次跨显示器迁移意图。
///
/// AI 初始编译不会创建该对象；它只由产品层的明确用户操作产生。
/// SourceRuleBeforeMigration 用于保留迁移前的规则证据，后续可以支持撤销/回迁。
/// </summary>
public sealed record DesktopCrossDisplayMigrationIntent
{
    public string StableId { get; }
    public string SourceDeviceName { get; }
    public string TargetDeviceName { get; }
    public Guid TargetZoneId { get; }
    public DesktopLayoutIconRule SourceRuleBeforeMigration { get; }

    public DesktopCrossDisplayMigrationIntent(
        string stableId,
        string sourceDeviceName,
        string targetDeviceName,
        Guid targetZoneId,
        DesktopLayoutIconRule sourceRuleBeforeMigration)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(sourceDeviceName))
            throw new ArgumentException("SourceDeviceName 不能为空。", nameof(sourceDeviceName));
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            throw new ArgumentException("TargetDeviceName 不能为空。", nameof(targetDeviceName));
        if (string.Equals(sourceDeviceName, targetDeviceName, StringComparison.Ordinal))
            throw new ArgumentException("跨屏迁移的源显示器与目标显示器不能相同。", nameof(targetDeviceName));
        if (targetZoneId == Guid.Empty)
            throw new ArgumentException("TargetZoneId 不能为 Guid.Empty。", nameof(targetZoneId));
        ArgumentNullException.ThrowIfNull(sourceRuleBeforeMigration);
        if (!string.Equals(sourceRuleBeforeMigration.StableId, stableId, StringComparison.Ordinal))
            throw new ArgumentException("SourceRuleBeforeMigration.StableId 必须与 StableId 一致。", nameof(sourceRuleBeforeMigration));

        StableId = stableId;
        SourceDeviceName = sourceDeviceName;
        TargetDeviceName = targetDeviceName;
        TargetZoneId = targetZoneId;
        SourceRuleBeforeMigration = sourceRuleBeforeMigration;
    }
}

/// <summary>
/// 一次跨屏编辑后的不可变结果。
/// 每块显示器仍然是一份既有 DesktopLayoutEditDraft；跨屏只存在于产品/Application 编排层。
/// </summary>
public sealed record DesktopCrossDisplayWorkspaceEditResult
{
    public IReadOnlyList<DesktopLayoutEditDraft> Drafts { get; }
    public IReadOnlyList<DesktopCrossDisplayMigrationIntent> Migrations { get; }

    public DesktopCrossDisplayWorkspaceEditResult(
        IReadOnlyList<DesktopLayoutEditDraft> drafts,
        IReadOnlyList<DesktopCrossDisplayMigrationIntent> migrations)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(migrations);
        Drafts = Array.AsReadOnly(drafts.ToArray());
        Migrations = Array.AsReadOnly(migrations.ToArray());
    }

    /// <summary>
    /// 返回某块目标显示器上由用户显式迁入的 StableId。
    /// 这组值后续作为 Preview 的 explicit-inbound 白名单，不能由 AI 自动生成。
    /// </summary>
    public IReadOnlySet<string> GetExplicitInboundStableIds(string targetDeviceName)
    {
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            throw new ArgumentException("TargetDeviceName 不能为空。", nameof(targetDeviceName));

        return Migrations
            .Where(item => string.Equals(item.TargetDeviceName, targetDeviceName, StringComparison.Ordinal))
            .Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// 多显示器工作区中的显式跨屏规则编辑器。
///
/// 只编辑内存 Draft/RuleSet，不包含 Preview、Candidate、Writer 或持久化 authority。
/// 源屏将迁出项目改成 Exclude，防止源屏布局继续把它作为默认项目重新放回；
/// 目标屏建立 ZoneOverride，明确指定落入哪个 Zone。
/// </summary>
public static class DesktopCrossDisplayWorkspaceEditor
{
    public static DesktopCrossDisplayWorkspaceEditResult Reassign(
        IReadOnlyList<DesktopLayoutEditDraft> drafts,
        IReadOnlyList<DesktopCrossDisplayMigrationIntent>? existingMigrations,
        string stableId,
        string sourceDeviceName,
        string targetDeviceName,
        Guid targetZoneId)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count < 2)
            throw new ArgumentException("跨屏迁移至少需要两块显示器草稿。", nameof(drafts));
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(sourceDeviceName))
            throw new ArgumentException("SourceDeviceName 不能为空。", nameof(sourceDeviceName));
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            throw new ArgumentException("TargetDeviceName 不能为空。", nameof(targetDeviceName));
        if (string.Equals(sourceDeviceName, targetDeviceName, StringComparison.Ordinal))
            throw new ArgumentException("跨屏迁移的源显示器与目标显示器不能相同。", nameof(targetDeviceName));
        if (targetZoneId == Guid.Empty)
            throw new ArgumentException("TargetZoneId 不能为 Guid.Empty。", nameof(targetZoneId));

        var byDevice = new Dictionary<string, DesktopLayoutEditDraft>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            ArgumentNullException.ThrowIfNull(draft);
            if (draft.TargetDisplayBinding.Kind != TargetDisplayBindingKind.ExactDeviceName
                || string.IsNullOrWhiteSpace(draft.TargetDisplayBinding.DeviceName))
            {
                throw new ArgumentException(
                    "跨屏工作区中的每份 Draft 必须绑定 ExactDeviceName。",
                    nameof(drafts));
            }

            if (!byDevice.TryAdd(draft.TargetDisplayBinding.DeviceName, draft))
                throw new ArgumentException(
                    $"显示器 {draft.TargetDisplayBinding.DeviceName} 在工作区中重复。",
                    nameof(drafts));
        }

        if (!byDevice.TryGetValue(sourceDeviceName, out var sourceDraft))
            throw new ArgumentException($"工作区中不存在源显示器 {sourceDeviceName}。", nameof(sourceDeviceName));
        if (!byDevice.TryGetValue(targetDeviceName, out var targetDraft))
            throw new ArgumentException($"工作区中不存在目标显示器 {targetDeviceName}。", nameof(targetDeviceName));
        if (!targetDraft.Zones.Any(zone => zone.ZoneId == targetZoneId))
            throw new ArgumentException(
                $"目标显示器 {targetDeviceName} 中不存在 Zone {targetZoneId:D}。",
                nameof(targetZoneId));

        var sourceRule = sourceDraft.RuleSet.Rules
            .SingleOrDefault(rule => string.Equals(rule.StableId, stableId, StringComparison.Ordinal));
        if (sourceRule is null)
        {
            throw new InvalidOperationException(
                $"源显示器 {sourceDeviceName} 的 Draft 没有 StableId \"{stableId}\" 的显式规则。" +
                "v1 跨屏迁移只接受已经被布局明确管理的项目，不从默认规则猜测用户意图。");
        }
        if (sourceRule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
        {
            throw new InvalidOperationException(
                $"StableId \"{stableId}\" 当前规则为 {sourceRule.Kind}。" +
                "请先解除 Pin/Exclude，再执行跨屏迁移。");
        }

        var updated = new List<DesktopLayoutEditDraft>(drafts.Count);
        foreach (var draft in drafts)
        {
            var deviceName = draft.TargetDisplayBinding.DeviceName!;
            var rules = draft.RuleSet.Rules
                .Where(rule => !string.Equals(rule.StableId, stableId, StringComparison.Ordinal))
                .ToList();

            if (string.Equals(deviceName, sourceDeviceName, StringComparison.Ordinal))
            {
                // 显式迁出后，源布局不得再次把该图标当默认项目塞回源屏。
                rules.Add(DesktopLayoutIconRule.ForExclude(stableId));
            }
            else if (string.Equals(deviceName, targetDeviceName, StringComparison.Ordinal))
            {
                rules.Add(DesktopLayoutIconRule.ForZoneOverride(stableId, targetZoneId));
            }

            updated.Add(CloneWithRuleSet(draft, new DesktopLayoutIconRuleSet(draft.TemplateId, rules)));
        }

        var migrations = (existingMigrations ?? Array.Empty<DesktopCrossDisplayMigrationIntent>())
            .Where(item => !string.Equals(item.StableId, stableId, StringComparison.Ordinal))
            .ToList();
        migrations.Add(new DesktopCrossDisplayMigrationIntent(
            stableId,
            sourceDeviceName,
            targetDeviceName,
            targetZoneId,
            sourceRule));

        migrations.Sort((a, b) => StringComparer.Ordinal.Compare(a.StableId, b.StableId));

        return new DesktopCrossDisplayWorkspaceEditResult(updated, migrations);
    }

    private static DesktopLayoutEditDraft CloneWithRuleSet(
        DesktopLayoutEditDraft draft,
        DesktopLayoutIconRuleSet ruleSet)
        => new(
            draft.TemplateId,
            draft.SourceVersion,
            draft.Name,
            draft.TargetDisplayBinding,
            draft.Zones,
            ruleSet);
}
