using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Poc.LayoutTemplates.Editor;

public enum SaveCoordinatorOutcome
{
    Success,
    FailedBeforeAnyWrite,
    FailedAndCompensated,
    FailedCompensationIncomplete,
    VerificationFailedAndCompensated,
    VerificationFailedCompensationIncomplete,
}

public sealed record CompensateResult
{
    public bool TemplateCompensationOk { get; init; }
    public bool RuleCompensationOk { get; init; }
    public string? TemplateCompensationError { get; init; }
    public string? RuleCompensationError { get; init; }
    public bool AllOk => TemplateCompensationOk && RuleCompensationOk;
}

public sealed class SaveCoordinatorResult
{
    public SaveCoordinatorOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public CompensateResult? CompensateResult { get; init; }
    public bool IsSuccess => Outcome == SaveCoordinatorOutcome.Success;
}

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
            return Fail("Template 为 null。");
        if (ruleSet is null)
            return Fail("RuleSet 为 null。");
        if (template.TemplateId != ruleSet.TemplateId)
            return Fail($"TemplateId 不一致：Template={template.TemplateId:D}，RuleSet={ruleSet.TemplateId:D}。");

        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
            {
                var zid = rule.TargetZoneId!.Value;
                var found = false;
                foreach (var z in template.Zones)
                    if (z.ZoneId == zid) { found = true; break; }
                if (!found)
                    return Fail($"ZoneOverride StableId=\"{rule.StableId}\" TargetZoneId=\"{zid:D}\" 不在 Template Zones 中。");
            }
        }

        // ═══ 读取保存前状态（fail-closed：异常不吞掉） ═══
        var tid = template.TemplateId;
        DesktopLayoutTemplate? previousTemplate;
        DesktopLayoutIconRuleSet? previousRuleSet;

        try { previousTemplate = await templateStore.GetAsync(tid, cancellationToken); }
        catch (Exception ex)
        {
            return Fail($"读取 previousTemplate 失败：{ex.Message}。未执行任何写入。");
        }

        try { previousRuleSet = await ruleStore.GetAsync(tid, cancellationToken); }
        catch (Exception ex)
        {
            return Fail($"读取 previousRuleSet 失败：{ex.Message}。未执行任何写入。");
        }

        // 状态一致性验证
        if (previousTemplate is null && previousRuleSet is not null)
            return Fail("数据不一致：previousTemplate=null 但 previousRuleSet 非 null。");
        if (previousTemplate is not null && previousRuleSet is null)
            return Fail("数据不一致：previousTemplate 非 null 但 previousRuleSet=null（应为 Empty）。");

        // ═══ 保存新模板 ═══
        try
        {
            await templateStore.SaveAsync(template, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail($"模板保存失败：{ex.Message}。RuleStore 未被调用。");
        }

        // ═══ 保存新规则 ═══
        string? ruleSaveError = null;
        try
        {
            await ruleStore.SaveAsync(ruleSet, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 模板已写入，必须补偿（使用独立令牌）
            var cr = await CompensateAsync(templateStore, ruleStore, tid,
                previousTemplate, previousRuleSet, CancellationToken.None);
            return new SaveCoordinatorResult
            {
                Outcome = cr.AllOk ? SaveCoordinatorOutcome.FailedAndCompensated
                                  : SaveCoordinatorOutcome.FailedCompensationIncomplete,
                Message = cr.AllOk
                    ? "写入后被取消，旧状态已恢复。"
                    : "写入后被取消，且恢复旧状态也失败！请重新加载或人工检查。",
                CompensateResult = cr
            };
        }
        catch (Exception ex)
        {
            ruleSaveError = ex.Message;
        }

        if (ruleSaveError is not null)
        {
            var cr = await CompensateAsync(templateStore, ruleStore, tid,
                previousTemplate, previousRuleSet, CancellationToken.None);
            return new SaveCoordinatorResult
            {
                Outcome = cr.AllOk ? SaveCoordinatorOutcome.FailedAndCompensated
                                  : SaveCoordinatorOutcome.FailedCompensationIncomplete,
                Message = cr.AllOk
                    ? $"规则保存失败（{ruleSaveError}），旧状态已恢复。"
                    : $"规则保存失败（{ruleSaveError}），且恢复旧状态也失败！",
                CompensateResult = cr
            };
        }

        // ═══ 回读验证 ═══
        string? verifyError = null;
        try
        {
            verifyError = await VerifyRoundTripAsync(
                templateStore, ruleStore, tid, template, ruleSet, cancellationToken);
        }
        catch (Exception ex)
        {
            verifyError = $"回读验证异常：{ex.Message}";
        }

        if (verifyError is not null)
        {
            var cr = await CompensateAsync(templateStore, ruleStore, tid,
                previousTemplate, previousRuleSet, CancellationToken.None);
            return new SaveCoordinatorResult
            {
                Outcome = cr.AllOk ? SaveCoordinatorOutcome.VerificationFailedAndCompensated
                                  : SaveCoordinatorOutcome.VerificationFailedCompensationIncomplete,
                Message = cr.AllOk
                    ? $"保存回读验证失败：{verifyError}。旧状态已恢复。"
                    : $"保存回读验证失败：{verifyError}，且恢复也失败！",
                CompensateResult = cr
            };
        }

        return new SaveCoordinatorResult
        {
            Outcome = SaveCoordinatorOutcome.Success,
            Message = "保存成功。"
        };
    }

    private static SaveCoordinatorResult Fail(string msg) => new()
    {
        Outcome = SaveCoordinatorOutcome.FailedBeforeAnyWrite,
        Message = msg
    };

    private static async Task<CompensateResult> CompensateAsync(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        Guid templateId,
        DesktopLayoutTemplate? previousTemplate,
        DesktopLayoutIconRuleSet? previousRuleSet,
        CancellationToken ct)
    {
        var result = new CompensateResult();

        // 恢复模板
        try
        {
            if (previousTemplate is not null)
                await templateStore.SaveAsync(previousTemplate, ct);
            else
                await templateStore.DeleteAsync(templateId, ct);
            result = result with { TemplateCompensationOk = true };
        }
        catch (Exception ex)
        {
            result = result with
            {
                TemplateCompensationOk = false,
                TemplateCompensationError = ex.Message
            };
        }

        // 恢复规则
        try
        {
            if (previousRuleSet is not null)
                await ruleStore.SaveAsync(previousRuleSet, ct);
            else
                await ruleStore.DeleteAsync(templateId, ct);
            result = result with { RuleCompensationOk = true };
        }
        catch (Exception ex)
        {
            result = result with
            {
                RuleCompensationOk = false,
                RuleCompensationError = ex.Message
            };
        }

        // 补偿后精确回读验证
        if (result.AllOk)
        {
            try
            {
                if (previousTemplate is not null)
                {
                    var verifyError = await VerifyRoundTripAsync(
                        templateStore, ruleStore, templateId,
                        previousTemplate,
                        previousRuleSet ?? DesktopLayoutIconRuleSet.Empty(templateId),
                        ct);
                    if (verifyError is not null)
                    {
                        // 判断是模板还是规则不一致
                        var tCheck2 = await templateStore.GetAsync(templateId, ct);
                        var rCheck2 = await ruleStore.GetAsync(templateId, ct);
                        bool tOk = tCheck2 is not null && tCheck2.TemplateId == previousTemplate.TemplateId;
                        bool rOk = rCheck2 is not null && rCheck2.TemplateId == previousTemplate.TemplateId;
                        return result with
                        {
                            TemplateCompensationOk = tOk,
                            TemplateCompensationError = tOk ? null : $"补偿后模板回读不一致：{verifyError}",
                            RuleCompensationOk = rOk,
                            RuleCompensationError = rOk ? null : $"补偿后规则回读不一致：{verifyError}"
                        };
                    }
                }
                else
                {
                    var tCheck = await templateStore.GetAsync(templateId, ct);
                    var rCheck = await ruleStore.GetAsync(templateId, ct);
                    if (tCheck is not null || rCheck is not null)
                    {
                        return result with
                        {
                            TemplateCompensationOk = false,
                            TemplateCompensationError = "补偿后模板/规则应被删除但仍存在。"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return result with
                {
                    TemplateCompensationOk = false,
                    TemplateCompensationError = $"补偿后回读异常：{ex.Message}"
                };
            }
        }

        return result;
    }

    private static async Task<string?> VerifyRoundTripAsync(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        Guid templateId,
        DesktopLayoutTemplate expectedTemplate,
        DesktopLayoutIconRuleSet expectedRuleSet,
        CancellationToken ct)
    {
        var loadedTemplate = await templateStore.GetAsync(templateId, ct);
        if (loadedTemplate is null) return "回读 Template 为 null。";
        if (loadedTemplate.TemplateId != expectedTemplate.TemplateId)
            return "回读 Template.TemplateId 不一致。";
        if (loadedTemplate.Name != expectedTemplate.Name)
            return $"回读 Name 不一致：\"{expectedTemplate.Name}\" vs \"{loadedTemplate.Name}\"。";
        if (loadedTemplate.Version != expectedTemplate.Version)
            return $"回读 Version 不一致：{expectedTemplate.Version} vs {loadedTemplate.Version}。";
        if (loadedTemplate.TargetDisplayBinding.Kind != expectedTemplate.TargetDisplayBinding.Kind)
            return "回读 Binding.Kind 不一致。";
        if (loadedTemplate.TargetDisplayBinding.DeviceName != expectedTemplate.TargetDisplayBinding.DeviceName)
            return "回读 Binding.DeviceName 不一致。";
        if (loadedTemplate.Zones.Count != expectedTemplate.Zones.Count)
            return $"回读 Zones 数量不一致：{expectedTemplate.Zones.Count} vs {loadedTemplate.Zones.Count}。";
        for (var i = 0; i < loadedTemplate.Zones.Count; i++)
        {
            var lz = loadedTemplate.Zones[i]; var ez = expectedTemplate.Zones[i];
            if (lz.ZoneId != ez.ZoneId) return $"回读 Zone[{i}].ZoneId 不一致。";
            if (lz.Name != ez.Name) return $"回读 Zone[{i}].Name 不一致。";
            if (lz.Bounds.Left != ez.Bounds.Left || lz.Bounds.Top != ez.Bounds.Top
                || lz.Bounds.Right != ez.Bounds.Right || lz.Bounds.Bottom != ez.Bounds.Bottom)
                return $"回读 Zone[{i}].Bounds 不一致。";
            if (lz.SortOrder != ez.SortOrder) return $"回读 Zone[{i}].SortOrder 不一致。";
            if (lz.PlacementFlow != ez.PlacementFlow) return $"回读 Zone[{i}].PlacementFlow 不一致。";
            if (lz.MaxItemCount != ez.MaxItemCount) return $"回读 Zone[{i}].MaxItemCount 不一致。";
        }

        var loadedRules = await ruleStore.GetAsync(templateId, ct);
        if (loadedRules is null) return "回读 RuleSet 为 null。";
        if (loadedRules.TemplateId != expectedRuleSet.TemplateId)
            return $"回读 RuleSet.TemplateId 不一致：{expectedRuleSet.TemplateId:D} vs {loadedRules.TemplateId:D}。";
        if (loadedRules.RuleCount != expectedRuleSet.RuleCount)
            return $"回读 RuleCount 不一致：{expectedRuleSet.RuleCount} vs {loadedRules.RuleCount}。";
        for (var i = 0; i < loadedRules.RuleCount; i++)
        {
            var lr = loadedRules.Rules[i]; var er = expectedRuleSet.Rules[i];
            if (!StringComparer.Ordinal.Equals(lr.StableId, er.StableId))
                return $"回读 Rule[{i}].StableId 不一致。";
            if (lr.Kind != er.Kind) return $"回读 Rule[{i}].Kind 不一致。";
            if (lr.TargetZoneId != er.TargetZoneId) return $"回读 Rule[{i}].TargetZoneId 不一致。";
        }

        return null;
    }
}
