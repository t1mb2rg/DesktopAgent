using System.Globalization;
using DesktopAgent.Application.DesktopAutomation;

namespace DesktopAgent.Poc.Acceptance;

public enum M3AutomaticAcceptanceFailureMode
{
    None = 0,
    FailBeforeWriterAtSafetySnapshot = 1,
}

/// <summary>
/// M3-B 实机 unattended write 验收专用配置。
/// 普通 PoC 不使用本配置；仅 Debug composition root 显式读取环境变量。
/// </summary>
public sealed record M3AutomaticAcceptanceOptions
{
    public const string ModeVariable = "DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MODE";
    public const string ArmVariable = "DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ARM";
    public const string MaxMovesVariable = "DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_MOVES";
    public const string MaxStepsVariable = "DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_STEPS";
    public const string AllowStagingVariable = "DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ALLOW_STAGING";
    public const string FailureModeVariable = "DESKTOPAGENT_M3_AUTO_ACCEPTANCE_FAILURE_MODE";

    public const string SupportedMode = "ValidatedOpen";
    public const string SupportedFailureMode = "FailBeforeWriterAtSafetySnapshot";
    public const string RequiredArmValue =
        "I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITES";

    public const int MaximumAcceptanceMoveLimit = 10;
    public const int MaximumAcceptanceStepLimit = 20;

    public bool IsEnabled { get; }
    public int MaxMoveCount { get; }
    public int MaxExecutionStepCount { get; }
    public bool AllowTemporaryStaging { get; }
    public M3AutomaticAcceptanceFailureMode FailureMode { get; }

    public bool InjectPreWriterFailure =>
        FailureMode == M3AutomaticAcceptanceFailureMode.FailBeforeWriterAtSafetySnapshot;

    public DesktopAutomationCandidateRiskPolicySettings RiskSettings =>
        new(MaxMoveCount, MaxExecutionStepCount, AllowTemporaryStaging);

    private M3AutomaticAcceptanceOptions(
        bool isEnabled,
        int maxMoveCount,
        int maxExecutionStepCount,
        bool allowTemporaryStaging,
        M3AutomaticAcceptanceFailureMode failureMode)
    {
        IsEnabled = isEnabled;
        MaxMoveCount = maxMoveCount;
        MaxExecutionStepCount = maxExecutionStepCount;
        AllowTemporaryStaging = allowTemporaryStaging;
        FailureMode = failureMode;
    }

    public static M3AutomaticAcceptanceOptions Disabled { get; } =
        new(
            false,
            M3AutomationPoCDefaults.RiskSettings.MaxMoveCount,
            M3AutomationPoCDefaults.RiskSettings.MaxExecutionStepCount,
            M3AutomationPoCDefaults.RiskSettings.AllowTemporaryStaging,
            M3AutomaticAcceptanceFailureMode.None);

    public static M3AutomaticAcceptanceOptions FromEnvironment() =>
        FromLookup(Environment.GetEnvironmentVariable);

    public static M3AutomaticAcceptanceOptions FromLookup(
        Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var mode = lookup(ModeVariable);
        var arm = lookup(ArmVariable);
        var maxMovesText = lookup(MaxMovesVariable);
        var maxStepsText = lookup(MaxStepsVariable);
        var allowStagingText = lookup(AllowStagingVariable);
        var failureModeText = lookup(FailureModeVariable);

        if (mode is null
            && arm is null
            && maxMovesText is null
            && maxStepsText is null
            && allowStagingText is null
            && failureModeText is null)
        {
            return Disabled;
        }

        if (!string.Equals(mode, SupportedMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ModeVariable} 必须精确为 {SupportedMode}。M3 automatic acceptance 未启用。");
        }

        if (!string.Equals(arm, RequiredArmValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ArmVariable} 未提供精确显式授权值。M3 automatic acceptance 未启用。");
        }

        var maxMoves = ParseBoundedPositiveInt(
            maxMovesText,
            MaxMovesVariable,
            MaximumAcceptanceMoveLimit);
        var maxSteps = ParseBoundedPositiveInt(
            maxStepsText,
            MaxStepsVariable,
            MaximumAcceptanceStepLimit);
        var allowStaging = ParseExplicitBoolean(
            allowStagingText,
            AllowStagingVariable);
        var failureMode = ParseFailureMode(failureModeText);

        return new M3AutomaticAcceptanceOptions(
            true,
            maxMoves,
            maxSteps,
            allowStaging,
            failureMode);
    }

    public DesktopAutomationRuntimePolicy CreateValidatedRuntimePolicy(
        TimeProvider timeProvider)
    {
        if (!IsEnabled)
            throw new InvalidOperationException(
                "Disabled M3 automatic acceptance 不能创建 ValidatedOpen runtime。");

        return new DesktopAutomationRuntimePolicy(
            M3AutomationPoCDefaults.RuntimeSettings,
            DesktopAutomationUnattendedGate.ValidatedOpen,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
    }

    public string Describe() => IsEnabled
        ? $"Mode={SupportedMode}; MaxMoves={MaxMoveCount}; MaxSteps={MaxExecutionStepCount}; "
            + $"AllowStaging={AllowTemporaryStaging}; FailureMode={FailureMode}"
        : "Disabled";

    private static int ParseBoundedPositiveInt(
        string? text,
        string variable,
        int maximum)
    {
        if (!int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"{variable} 必须为 1..{maximum} 的十进制正整数。M3 automatic acceptance 未启用。");
        }

        return value;
    }

    private static bool ParseExplicitBoolean(
        string? text,
        string variable) =>
        text switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException(
                $"{variable} 必须精确为 true 或 false。M3 automatic acceptance 未启用。"),
        };

    private static M3AutomaticAcceptanceFailureMode ParseFailureMode(string? text) =>
        text switch
        {
            null => M3AutomaticAcceptanceFailureMode.None,
            SupportedFailureMode =>
                M3AutomaticAcceptanceFailureMode.FailBeforeWriterAtSafetySnapshot,
            _ => throw new InvalidOperationException(
                $"{FailureModeVariable} 若提供，必须精确为 {SupportedFailureMode}。"
                + "M3 automatic acceptance 未启用。"),
        };
}
