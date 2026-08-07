using System.Globalization;

namespace DesktopAgent.Poc.Acceptance;

/// <summary>
/// M2 实机验收专用的受控故障注入配置。
/// 仅由 Debug 组合根读取；Release 组合根不会读取这些环境变量。
/// </summary>
public sealed record AcceptanceFaultInjectionOptions
{
    public const string ModeVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_MODE";
    public const string MoveCallVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_CALL";
    public const string StableIdVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_STABLE_ID";
    public const string ArmVariable = "DESKTOPAGENT_ACCEPTANCE_FAULT_ARM";

    public const string SupportedMode = "AfterExactWriteBeforeCheckpoint";
    public const string RequiredArmValue = "I_UNDERSTAND_THIS_WILL_TERMINATE_DESKTOPAGENT";

    public bool IsEnabled { get; }
    public int MoveCallIndex { get; }
    public string StableId { get; }

    private AcceptanceFaultInjectionOptions(bool isEnabled, int moveCallIndex, string stableId)
    {
        IsEnabled = isEnabled;
        MoveCallIndex = moveCallIndex;
        StableId = stableId;
    }

    public static AcceptanceFaultInjectionOptions Disabled { get; } = new(false, 0, string.Empty);

    public static AcceptanceFaultInjectionOptions FromEnvironment() =>
        FromLookup(Environment.GetEnvironmentVariable);

    public static AcceptanceFaultInjectionOptions FromLookup(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var mode = lookup(ModeVariable);
        var callText = lookup(MoveCallVariable);
        var stableId = lookup(StableIdVariable);
        var arm = lookup(ArmVariable);

        if (mode is null && callText is null && stableId is null && arm is null)
            return Disabled;

        if (!string.Equals(mode, SupportedMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ModeVariable} 必须精确为 {SupportedMode}。故障注入未启用。");
        }

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

        if (!string.Equals(arm, RequiredArmValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ArmVariable} 未提供精确的显式授权值。故障注入未启用。");
        }

        return new AcceptanceFaultInjectionOptions(true, callIndex, stableId);
    }

    public string Describe() => IsEnabled
        ? $"{SupportedMode}; MoveCall={MoveCallIndex}; StableId={StableId}"
        : "Disabled";
}
