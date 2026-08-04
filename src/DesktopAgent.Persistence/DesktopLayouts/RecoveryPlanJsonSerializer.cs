using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.DesktopLayouts.Restore;

namespace DesktopAgent.Persistence.DesktopLayouts;

/// <summary>
/// 内部辅助方法：将 RestorePlan / ExecutionPlan 序列化为版本化 JSON payload。
/// 使用 System.Text.Json 原生记录序列化（无需修改 Core 类型）。
/// </summary>
internal static class RecoveryPlanJsonSerializer
{
    internal const int CurrentPlanPayloadFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>序列化 RestorePlan 为 JSON 字符串。</summary>
    internal static string SerializeRestorePlan(TargetDisplayLayoutRestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.Serialize(plan, Options);
    }

    /// <summary>反序列化 RestorePlan。格式错误或无效数据时抛出。</summary>
    internal static TargetDisplayLayoutRestorePlan DeserializeRestorePlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("RestorePlan JSON 不能为 null 或空白。", nameof(json));

        var plan = JsonSerializer.Deserialize<TargetDisplayLayoutRestorePlan>(json, Options);
        if (plan is null)
            throw new InvalidDataException("RestorePlan JSON 反序列化返回 null。");
        return plan;
    }

    /// <summary>序列化 ExecutionPlan 为 JSON 字符串。</summary>
    internal static string SerializeExecutionPlan(
        Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.Serialize(plan, Options);
    }

    /// <summary>反序列化 ExecutionPlan。格式错误或无效数据时抛出。</summary>
    internal static Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionPlan
        DeserializeExecutionPlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("ExecutionPlan JSON 不能为 null 或空白。", nameof(json));

        var plan = JsonSerializer.Deserialize<
            Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionPlan>(json, Options);
        if (plan is null)
            throw new InvalidDataException("ExecutionPlan JSON 反序列化返回 null。");
        return plan;
    }

    /// <summary>序列化 VerificationResult 为 JSON 字符串（null 返回 null）。</summary>
    internal static string? SerializeVerificationResult(
        Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionVerificationResult? result)
    {
        if (result is null) return null;
        return JsonSerializer.Serialize(result, Options);
    }

    /// <summary>反序列化 VerificationResult（null/空白返回 null）。</summary>
    internal static Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionVerificationResult?
        DeserializeVerificationResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var result = JsonSerializer.Deserialize<
            Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionVerificationResult>(json, Options);
        return result;
    }
}
