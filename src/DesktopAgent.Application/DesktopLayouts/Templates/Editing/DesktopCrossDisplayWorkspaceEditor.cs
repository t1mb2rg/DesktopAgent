using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 用户显式发起的一次跨显示器迁移意图。
/// AI 初始编译不会创建该对象；它只由产品层的明确用户操作产生。
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
/// 只编辑内存 Draft/RuleSet，不包含 Preview、Candidate、Writer 或持久化 authority。
///
/// v1 规定：一个 StableId 同时最多一笔 pending migration。迁移尚未 Apply 前不能再次改投第三块屏，
/// 避免“逻辑来源屏”与真实物理来源屏分叉后丢失源屏 Exclude 保护。
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

        var migrations = (existingMigrations ?? Array.Empty<DesktopCrossDisplayMigrationIntent>()).ToList();
        var pending = migrations.SingleOrDefault(item =>
            string.Equals(item.StableId, stableId, StringComparison.Ordinal));
        if (pending is not null)
        {
            throw new InvalidOperationException(
                $"StableId \"{stableId}\" 已有一笔待执行跨屏迁移：" +
                $"{pending.SourceDeviceName} → {pending.TargetDeviceName}。" +
                "请先应用或取消当前迁移，再重新选择目标屏。");
        }

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

        migrations.Add(new DesktopCrossDisplayMigrationIntent(
            stableId,
            sourceDeviceName,
            targetDeviceName,
            targetZoneId,
            sourceRule));
        migrations.Sort((a, b) => StringComparer.Ordinal.Compare(a.StableId, b.StableId));

        var updated = new List<DesktopLayoutEditDraft>(drafts.Count);
        foreach (var draft in drafts)
        {
            var deviceName = draft.TargetDisplayBinding.DeviceName!;
            var rules = draft.RuleSet.Rules
                .Where(rule => !string.Equals(rule.StableId, stableId, StringComparison.Ordinal))
                .ToList();

            if (string.Equals(deviceName, sourceDeviceName, StringComparison.Ordinal))
                rules.Add(DesktopLayoutIconRule.ForExclude(stableId));
            else if (string.Equals(deviceName, targetDeviceName, StringComparison.Ordinal))
                rules.Add(DesktopLayoutIconRule.ForZoneOverride(stableId, targetZoneId));

            var inbound = migrations
                .Where(item => string.Equals(item.TargetDeviceName, deviceName, StringComparison.Ordinal))
                .Select(static item => item.StableId)
                .ToArray();
            updated.Add(CloneWithRuleSet(
                draft,
                new DesktopLayoutIconRuleSet(draft.TemplateId, rules),
                inbound));
        }

        return new DesktopCrossDisplayWorkspaceEditResult(updated, migrations);
    }

    private static DesktopLayoutEditDraft CloneWithRuleSet(
        DesktopLayoutEditDraft draft,
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlyCollection<string> explicitInboundStableIds)
        => new(
            draft.TemplateId,
            draft.SourceVersion,
            draft.Name,
            draft.TargetDisplayBinding,
            draft.Zones,
            ruleSet,
            explicitInboundStableIds);
}
