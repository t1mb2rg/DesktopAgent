using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

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

/// <summary>
/// Template + RuleSet 的逻辑原子保存边界。底层两个 Store 不能共享事务时，
/// 通过保存前快照、补偿写和精确回读验证避免留下半套产品布局。
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
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);

        if (template is null)
            return Fail("Template 为 null。");
        if (ruleSet is null)
            return Fail("RuleSet 为 null。");
        if (template.TemplateId != ruleSet.TemplateId)
            return Fail($"TemplateId 不一致：Template={template.TemplateId:D}，RuleSet={ruleSet.TemplateId:D}。");

        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
                continue;

            var zoneId = rule.TargetZoneId!.Value;
            if (!template.Zones.Any(zone => zone.ZoneId == zoneId))
            {
                return Fail(
                    $"ZoneOverride StableId=\"{rule.StableId}\" TargetZoneId=\"{zoneId:D}\" 不在 Template Zones 中。");
            }
        }

        var templateId = template.TemplateId;
        DesktopLayoutTemplate? previousTemplate;
        DesktopLayoutIconRuleSet? previousRuleSet;

        try
        {
            previousTemplate = await templateStore.GetAsync(templateId, cancellationToken);
        }
        catch (Exception ex)
        {
            return Fail($"读取 previousTemplate 失败：{ex.Message}。未执行任何写入。");
        }

        try
        {
            previousRuleSet = await ruleStore.GetAsync(templateId, cancellationToken);
        }
        catch (Exception ex)
        {
            return Fail($"读取 previousRuleSet 失败：{ex.Message}。未执行任何写入。");
        }

        if (previousTemplate is null && previousRuleSet is not null)
            return Fail("数据不一致：previousTemplate=null 但 previousRuleSet 非 null。");
        if (previousTemplate is not null && previousRuleSet is null)
            return Fail("数据不一致：previousTemplate 非 null 但 previousRuleSet=null（应为 Empty）。");

        try
        {
            await templateStore.SaveAsync(template, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"模板保存失败：{ex.Message}。RuleStore 未被调用。");
        }

        string? ruleSaveError = null;
        try
        {
            await ruleStore.SaveAsync(ruleSet, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var compensation = await CompensateAsync(
                templateStore,
                ruleStore,
                templateId,
                previousTemplate,
                previousRuleSet,
                CancellationToken.None);

            return new SaveCoordinatorResult
            {
                Outcome = compensation.AllOk
                    ? SaveCoordinatorOutcome.FailedAndCompensated
                    : SaveCoordinatorOutcome.FailedCompensationIncomplete,
                Message = compensation.AllOk
                    ? "写入后被取消，旧状态已恢复。"
                    : "写入后被取消，且恢复旧状态也失败！请重新加载或人工检查。",
                CompensateResult = compensation,
            };
        }
        catch (Exception ex)
        {
            ruleSaveError = ex.Message;
        }

        if (ruleSaveError is not null)
        {
            var compensation = await CompensateAsync(
                templateStore,
                ruleStore,
                templateId,
                previousTemplate,
                previousRuleSet,
                CancellationToken.None);

            return new SaveCoordinatorResult
            {
                Outcome = compensation.AllOk
                    ? SaveCoordinatorOutcome.FailedAndCompensated
                    : SaveCoordinatorOutcome.FailedCompensationIncomplete,
                Message = compensation.AllOk
                    ? $"规则保存失败（{ruleSaveError}），旧状态已恢复。"
                    : $"规则保存失败（{ruleSaveError}），且恢复旧状态也失败！",
                CompensateResult = compensation,
            };
        }

        string? verifyError;
        try
        {
            verifyError = await VerifyRoundTripAsync(
                templateStore,
                ruleStore,
                templateId,
                template,
                ruleSet,
                cancellationToken);
        }
        catch (Exception ex)
        {
            verifyError = $"回读验证异常：{ex.Message}";
        }

        if (verifyError is not null)
        {
            var compensation = await CompensateAsync(
                templateStore,
                ruleStore,
                templateId,
                previousTemplate,
                previousRuleSet,
                CancellationToken.None);

            return new SaveCoordinatorResult
            {
                Outcome = compensation.AllOk
                    ? SaveCoordinatorOutcome.VerificationFailedAndCompensated
                    : SaveCoordinatorOutcome.VerificationFailedCompensationIncomplete,
                Message = compensation.AllOk
                    ? $"保存回读验证失败：{verifyError}。旧状态已恢复。"
                    : $"保存回读验证失败：{verifyError}，且恢复也失败！",
                CompensateResult = compensation,
            };
        }

        return new SaveCoordinatorResult
        {
            Outcome = SaveCoordinatorOutcome.Success,
            Message = "保存成功。",
        };
    }

    private static SaveCoordinatorResult Fail(string message) => new()
    {
        Outcome = SaveCoordinatorOutcome.FailedBeforeAnyWrite,
        Message = message,
    };

    private static async Task<CompensateResult> CompensateAsync(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        Guid templateId,
        DesktopLayoutTemplate? previousTemplate,
        DesktopLayoutIconRuleSet? previousRuleSet,
        CancellationToken cancellationToken)
    {
        var result = new CompensateResult();

        try
        {
            if (previousTemplate is not null)
                await templateStore.SaveAsync(previousTemplate, cancellationToken);
            else
                await templateStore.DeleteAsync(templateId, cancellationToken);
            result = result with { TemplateCompensationOk = true };
        }
        catch (Exception ex)
        {
            result = result with
            {
                TemplateCompensationOk = false,
                TemplateCompensationError = ex.Message,
            };
        }

        try
        {
            if (previousRuleSet is not null)
                await ruleStore.SaveAsync(previousRuleSet, cancellationToken);
            else
                await ruleStore.DeleteAsync(templateId, cancellationToken);
            result = result with { RuleCompensationOk = true };
        }
        catch (Exception ex)
        {
            result = result with
            {
                RuleCompensationOk = false,
                RuleCompensationError = ex.Message,
            };
        }

        if (!result.AllOk)
            return result;

        try
        {
            if (previousTemplate is null)
            {
                var restoredTemplate = await templateStore.GetAsync(templateId, cancellationToken);
                var restoredRules = await ruleStore.GetAsync(templateId, cancellationToken);
                if (restoredTemplate is null && restoredRules is null)
                    return result;

                return result with
                {
                    TemplateCompensationOk = false,
                    TemplateCompensationError = "补偿后模板/规则应被删除但仍存在。",
                };
            }

            var verifyError = await VerifyRoundTripAsync(
                templateStore,
                ruleStore,
                templateId,
                previousTemplate,
                previousRuleSet ?? DesktopLayoutIconRuleSet.Empty(templateId),
                cancellationToken);

            if (verifyError is null)
                return result;

            var templateCheck = await templateStore.GetAsync(templateId, cancellationToken);
            var ruleCheck = await ruleStore.GetAsync(templateId, cancellationToken);
            var templateOk = templateCheck is not null && templateCheck.TemplateId == previousTemplate.TemplateId;
            var rulesOk = ruleCheck is not null && ruleCheck.TemplateId == previousTemplate.TemplateId;

            return result with
            {
                TemplateCompensationOk = templateOk,
                TemplateCompensationError = templateOk ? null : $"补偿后模板回读不一致：{verifyError}",
                RuleCompensationOk = rulesOk,
                RuleCompensationError = rulesOk ? null : $"补偿后规则回读不一致：{verifyError}",
            };
        }
        catch (Exception ex)
        {
            return result with
            {
                TemplateCompensationOk = false,
                TemplateCompensationError = $"补偿后回读异常：{ex.Message}",
            };
        }
    }

    private static async Task<string?> VerifyRoundTripAsync(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        Guid templateId,
        DesktopLayoutTemplate expectedTemplate,
        DesktopLayoutIconRuleSet expectedRuleSet,
        CancellationToken cancellationToken)
    {
        var loadedTemplate = await templateStore.GetAsync(templateId, cancellationToken);
        if (loadedTemplate is null) return "回读 Template 为 null。";
        if (loadedTemplate.TemplateId != expectedTemplate.TemplateId) return "回读 Template.TemplateId 不一致。";
        if (loadedTemplate.Name != expectedTemplate.Name) return "回读 Name 不一致。";
        if (loadedTemplate.Version != expectedTemplate.Version) return "回读 Version 不一致。";
        if (loadedTemplate.TargetDisplayBinding.Kind != expectedTemplate.TargetDisplayBinding.Kind) return "回读 Binding.Kind 不一致。";
        if (loadedTemplate.TargetDisplayBinding.DeviceName != expectedTemplate.TargetDisplayBinding.DeviceName) return "回读 Binding.DeviceName 不一致。";
        if (loadedTemplate.Zones.Count != expectedTemplate.Zones.Count) return "回读 Zones 数量不一致。";

        for (var i = 0; i < loadedTemplate.Zones.Count; i++)
        {
            var actual = loadedTemplate.Zones[i];
            var expected = expectedTemplate.Zones[i];
            if (actual.ZoneId != expected.ZoneId) return $"回读 Zone[{i}].ZoneId 不一致。";
            if (actual.Name != expected.Name) return $"回读 Zone[{i}].Name 不一致。";
            if (actual.Bounds != expected.Bounds) return $"回读 Zone[{i}].Bounds 不一致。";
            if (actual.SortOrder != expected.SortOrder) return $"回读 Zone[{i}].SortOrder 不一致。";
            if (actual.PlacementFlow != expected.PlacementFlow) return $"回读 Zone[{i}].PlacementFlow 不一致。";
            if (actual.MaxItemCount != expected.MaxItemCount) return $"回读 Zone[{i}].MaxItemCount 不一致。";
        }

        var loadedRules = await ruleStore.GetAsync(templateId, cancellationToken);
        if (loadedRules is null) return "回读 RuleSet 为 null。";
        if (loadedRules.TemplateId != expectedRuleSet.TemplateId) return "回读 RuleSet.TemplateId 不一致。";
        if (loadedRules.RuleCount != expectedRuleSet.RuleCount) return "回读 RuleCount 不一致。";

        for (var i = 0; i < loadedRules.RuleCount; i++)
        {
            var actual = loadedRules.Rules[i];
            var expected = expectedRuleSet.Rules[i];
            if (!StringComparer.Ordinal.Equals(actual.StableId, expected.StableId)) return $"回读 Rule[{i}].StableId 不一致。";
            if (actual.Kind != expected.Kind) return $"回读 Rule[{i}].Kind 不一致。";
            if (actual.TargetZoneId != expected.TargetZoneId) return $"回读 Rule[{i}].TargetZoneId 不一致。";
        }

        return null;
    }
}
