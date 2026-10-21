using System.Globalization;

namespace DesktopAgent.Poc.Acceptance;

public enum AcceptanceFaultInjectionBehavior
{
    HardCrash = 0,
    SoftError = 1,
}

/// <summary>
/// M2 实机验收专用的受控故障注入配置。
/// 仅由 Debug 组合根读取；Release 组合根不会读取这些环境变量。
/// </summary>
public sealed record AcceptanceFaultInjectionOptions
{
    public const string ModeVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_MODE";
    public const string BehaviorVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_BEHAVIOR";
    public const string MoveCallVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_CALL";
    public const string StableIdVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_STABLE_ID";
    public const string ArmVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_ARM";

    public const string SupportedMode = "AfterExactWriteBeforeCheckpoint";
    public const string HardCrashBehaviorValue = "HardCrash";
    public const string SoftErrorBehaviorValue = "SoftError";
    public const string RequiredHardCrashArmValue = "I_UNDERSTAND_THIS_WILL_TERMINATE_DESKTOPAGENT";
    public const string RequiredSoftErrorArmValue = "I_UNDERSTAND_THIS_MAY_REQUIRE_RECOVERY";

    // 兼容昨晚已有的验收脚本与测试命名。
    public const string RequiredArmValue = RequiredHardCrashArmValue;

    public bool IsEnabled { get; }
    public AcceptanceFaultInjectionBehavior Behavior { get; }
    public int MoveCallIndex { get; }
    public string StableId { get; }

    public bool WillTerminateProcess =>
        IsEnabled && Behavior == AcceptanceFaultInjectionBehavior.HardCrash;

    private AcceptanceFaultInjectionOptions(
        bool isEnabled,
        AcceptanceFaultInjectionBehavior behavior,
        int moveCallIndex,
        string stableId)
    {
        IsEnabled = isEnabled;
        Behavior = behavior;
        MoveCallIndex = moveCallIndex;
        StableId = stableId;
    }

    public static AcceptanceFaultInjectionOptions Disabled { get; } = new(
        false,
        AcceptanceFaultInjectionBehavior.HardCrash,
        0,
        string.Empty);

    public static AcceptanceFaultInjectionOptions FromEnvironment() =>
        FromLookup(Environment.GetEnvironmentVariable);

    public static AcceptanceFaultInjectionOptions FromLookup(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var mode = lookup(ModeVariable);
        var behaviorText = lookup(BehaviorVariable);
        var callText = lookup(MoveCallVariable);
        var stableId = lookup(StableIdVariable);
        var arm = lookup(ArmVariable);

        if (mode is null && behaviorText is null && callText is null && stableId is null && arm is null)
            return Disabled;

        if (!string.Equals(mode, SupportedMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ModeVariable} 必须精确为 {SupportedMode}。故障注入未启用。");
        }

        var behavior = ParseBehavior(behaviorText);

        if (!int.TryParse(callText, NumberStyles.None, CultureInfo.InvariantCulture, out var callIndex)
            || callIndex <= 0)
        {
            throw new InvalidOperationException(
                $"{MoveCallVariable} 必须为从 1 开始的正整数。故障注入未启用。");
        }

        if (string.IsNullOrWhiteSpace(stableId))
        {
            throw new InvalidOperationException(
                $"{StableIdVariable} 必须明确指定要命中的 StableId。故障注入未启用。");
        }

        var requiredArmValue = behavior switch
        {
            AcceptanceFaultInjectionBehavior.HardCrash => RequiredHardCrashArmValue,
            AcceptanceFaultInjectionBehavior.SoftError => RequiredSoftErrorArmValue,
            _ => throw new InvalidOperationException("未知故障注入行为。"),
        };

        if (!string.Equals(arm, requiredArmValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ArmVariable} 未提供与 {behavior} 对应的精确显式授权值。故障注入未启用。");
        }

        return new AcceptanceFaultInjectionOptions(true, behavior, callIndex, stableId);
    }

    public string Describe() => IsEnabled
        ? $"{SupportedMode}; Behavior={Behavior}; MoveCall={MoveCallIndex}; StableId={StableId}"
        : "Disabled";

    private static AcceptanceFaultInjectionBehavior ParseBehavior(string? text)
    {
        // 兼容昨晚已有配置：未提供 BEHAVIOR 时继续保持原来的真崩溃语义。
        if (text is null || string.Equals(text, HardCrashBehaviorValue, StringComparison.Ordinal))
            return AcceptanceFaultInjectionBehavior.HardCrash;

        if (string.Equals(text, SoftErrorBehaviorValue, StringComparison.Ordinal))
            return AcceptanceFaultInjectionBehavior.SoftError;

        throw new InvalidOperationException(
            $"{BehaviorVariable} 只允许 {HardCrashBehaviorValue} 或 {SoftErrorBehaviorValue}。故障注入未启用。");
    }
}
