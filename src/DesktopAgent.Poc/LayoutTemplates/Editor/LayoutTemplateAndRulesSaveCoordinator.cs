using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Poc.LayoutTemplates.Editor;

/// <summary>双 Store 保存协调结果。</summary>
public enum SaveCoordinatorOutcome
{
    Success,
    FailedBeforeAnyWrite,
    FailedAndCompensated,
    FailedCompensationIncomplete,
    VerificationFailedAndCompensated,
    VerificationFailedCompensationIncomplete,
}

/// <summary>保存协调结构化结果。</summary>
public sealed class SaveCoordinatorResult
{
    public SaveCoordinatorOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public bool IsSuccess => Outcome == SaveCoordinatorOutcome.Success;
}

/// <summary>
/// 双 Store 保存协调器。验证 Template/RuleSet 一致性后，
/// 先保存模板，再保存规则。任一步失败则尝试补偿恢复旧状态。
/// </summary>
public static class LayoutTemplateAndRulesSaveCoordinator
{
    public static async Task<SaveCoordinatorResult> SaveAsync(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        DesktopLayoutTemplate template,
        DesktopLayoutIconRuleSet ruleSet,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ═══ 保存前验证 ═══
        if (template is null)
            return new SaveCoordinatorResult
            {
                Outcome = SaveCoordinatorOutcome.FailedBeforeAnyWrite,
                Message = "Template 为 null。"
            };
        if (ruleSet is null)
            return new SaveCoordinatorResult
            {
                Outcome = SaveCoordinatorOutcome.FailedBeforeAnyWrite,
                Message = "RuleSet 为 null。"
            };
        if (template.TemplateId != ruleSet.TemplateId)
            return new SaveCoordinatorResult
            {
                Outcome = SaveCoordinatorOutcome.FailedBeforeAnyWrite,
                Message = $"TemplateId 不一致：Template={template.TemplateId:D}，RuleSet={ruleSet.TemplateId:D}。"
            };

        // 验证所有 ZoneOverride TargetZoneId 存在于待保存模板
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
            {
                var zid = rule.TargetZoneId!.Value;
                var found = false;
                foreach (var z in template.Zones)
                {
                    if (z.ZoneId == zid) { found = true; break; }
                }
                if (!found)
                    return new SaveCoordinatorResult
                    {
                        Outcome = SaveCoordinatorOutcome.FailedBeforeAnyWrite,
                        Message = $"ZoneOverride StableId=\"{rule.StableId}\" TargetZoneId=\"{zid:D}\" 不在 Template Zones 中。"
                    };
            }
        }

        // ═══ 读取保存前状态 ═══
        var tid = template.TemplateId;
        DesktopLayoutTemplate? previousTemplate = null;
        DesktopLayoutIconRuleSet? previousRuleSet = null;

        try { previousTemplate = await templateStore.GetAsync(tid, cancellationToken); }
        catch { /* 读取失败不影响保存尝试 */ }

        try { previousRuleSet = await ruleStore.GetAsync(tid, cancellationToken); }
        catch { /* 同上 */ }

        // ═══ 步骤 B：保存新模板 ═══
        try
        {
            await templateStore.SaveAsync(template, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SaveCoordinatorResult
            {
                Outcome = SaveCoordinatorOutcome.FailedBeforeAnyWrite,
                Message = $"模板保存失败：{ex.Message}。RuleStore 未被调用。"
            };
        }

        // ═══ 步骤 C：保存新规则 ═══
        bool ruleSaveFailed = false;
        string? ruleSaveError = null;
        try
        {
            await ruleStore.SaveAsync(ruleSet, cancellationToken);
        }
        catch (Exception ex)
        {
            ruleSaveFailed = true;
            ruleSaveError = ex.Message;
        }

        if (ruleSaveFailed)
        {
            // 补偿：恢复旧模板和旧规则
            var compResult = await CompensateAsync(
                templateStore, ruleStore, tid,
                previousTemplate, previousRuleSet, cancellationToken);
            return new SaveCoordinatorResult
            {
                Outcome = compResult
                    ? SaveCoordinatorOutcome.FailedAndCompensated
                    : SaveCoordinatorOutcome.FailedCompensationIncomplete,
                Message = compResult
                    ? $"规则保存失败（{ruleSaveError}），旧状态已恢复。"
                    : $"规则保存失败（{ruleSaveError}），且恢复旧状态也失败！请重新加载或人工检查。"
            };
        }

        // ═══ 步骤 D-E：回读验证 ═══
        var verifyResult = await VerifyRoundTripAsync(
            templateStore, ruleStore, tid, template, ruleSet, cancellationToken);

        if (verifyResult != null)
        {
            // 验证失败 → 补偿
            var compResult = await CompensateAsync(
                templateStore, ruleStore, tid,
                previousTemplate, previousRuleSet, cancellationToken);
            return new SaveCoordinatorResult
            {
                Outcome = compResult
                    ? SaveCoordinatorOutcome.VerificationFailedAndCompensated
                    : SaveCoordinatorOutcome.VerificationFailedCompensationIncomplete,
                Message = compResult
                    ? $"保存回读验证失败：{verifyResult}。旧状态已恢复。"
                    : $"保存回读验证失败：{verifyResult}，且恢复旧状态也失败！请重新加载或人工检查。"
            };
        }

        return new SaveCoordinatorResult
        {
            Outcome = SaveCoordinatorOutcome.Success,
            Message = "保存成功。"
        };
    }

    private static async Task<bool> CompensateAsync(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        Guid templateId,
        DesktopLayoutTemplate? previousTemplate,
        DesktopLayoutIconRuleSet? previousRuleSet,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        try
        {
            if (previousTemplate is not null)
                await templateStore.SaveAsync(previousTemplate, ct);
            else
                await templateStore.DeleteAsync(templateId, ct);

            if (previousRuleSet is not null)
                await ruleStore.SaveAsync(previousRuleSet, ct);
            else
                await ruleStore.DeleteAsync(templateId, ct);

            return true;
        }
        catch { return false; }
    }

    /// <summary>回读验证。成功返回 null，失败返回错误描述。</summary>
    private static async Task<string?> VerifyRoundTripAsync(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        Guid templateId,
        DesktopLayoutTemplate expectedTemplate,
        DesktopLayoutIconRuleSet expectedRuleSet,
        CancellationToken ct)
    {
        // 验证模板
        var loadedTemplate = await templateStore.GetAsync(templateId, ct);
        if (loadedTemplate is null)
            return "回读 Template 为 null。";
        if (loadedTemplate.TemplateId != expectedTemplate.TemplateId)
            return "回读 Template.TemplateId 不一致。";
        if (loadedTemplate.Name != expectedTemplate.Name)
            return $"回读 Template.Name 不一致：期望 \"{expectedTemplate.Name}\"，实际 \"{loadedTemplate.Name}\"。";
        if (loadedTemplate.Version != expectedTemplate.Version)
            return $"回读 Template.Version 不一致：期望 {expectedTemplate.Version}，实际 {loadedTemplate.Version}。";
        if (loadedTemplate.Zones.Count != expectedTemplate.Zones.Count)
            return $"回读 Template.Zones 数量不一致：期望 {expectedTemplate.Zones.Count}，实际 {loadedTemplate.Zones.Count}。";

        // 验证规则
        var loadedRules = await ruleStore.GetAsync(templateId, ct);
        if (loadedRules is null)
            return "回读 RuleSet 为 null。";
        if (loadedRules.RuleCount != expectedRuleSet.RuleCount)
            return $"回读 RuleCount 不一致：期望 {expectedRuleSet.RuleCount}，实际 {loadedRules.RuleCount}。";
        for (var i = 0; i < loadedRules.RuleCount; i++)
        {
            var lr = loadedRules.Rules[i];
            var er = expectedRuleSet.Rules[i];
            if (lr.StableId != er.StableId || lr.Kind != er.Kind
                || lr.TargetZoneId != er.TargetZoneId)
                return $"回读 Rule[{i}] 不一致。";
        }

        return null; // 验证通过
    }
}
