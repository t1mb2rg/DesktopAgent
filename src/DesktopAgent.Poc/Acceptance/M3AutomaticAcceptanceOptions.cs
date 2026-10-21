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
    public const string RequestedModeVariable =
        "DESKTOPAGENT_M3_AUTO_ACCEPTANCE_REQUESTED_MODE";

    public const string SupportedMode = "ValidatedOpen";
    public const string SupportedFailureMode = "FailBeforeWriterAtSafetySnapshot";
    public const string AutomaticRequestedModeValue = "Automatic";
    public const string SuggestionRequestedModeValue = "Suggestion";
    public const string RequiredArmValue =
        "I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITES";

    public const int MaximumAcceptanceMoveLimit = 10;
    public const int MaximumAcceptanceStepLimit = 20;

    public bool IsEnabled { get; }
    public int MaxMoveCount { get; }
    public int MaxExecutionStepCount { get; }
    public bool AllowTemporaryStaging { get; }
    public M3AutomaticAcceptanceFailureMode FailureMode { get; }

    /// <summary>
    /// 本次验收进程启动时的请求模式。默认 Suggestion（与产品一致）；
    /// 只有显式给定 <see cref="AutomaticRequestedModeValue"/> 时才以 Automatic 启动，
    /// 用于在没有 tray 交互的验收环境里等价地完成「用户从 tray 选择 Automatic」这一步。
    /// 它只是一次等价输入：真正的安全边界仍是 ValidatedOpen gate、逐屏实测证据与站定授权。
    /// </summary>
    public DesktopAutomationMode RequestedMode { get; }

    public bool InjectPreWriterFailure =>
        FailureMode == M3AutomaticAcceptanceFailureMode.FailBeforeWriterAtSafetySnapshot;

    public DesktopAutomationCandidateRiskPolicySettings RiskSettings =>
        new(MaxMoveCount, MaxExecutionStepCount, AllowTemporaryStaging);

    private M3AutomaticAcceptanceOptions(
        bool isEnabled,
        int maxMoveCount,
        int maxExecutionStepCount,
        bool allowTemporaryStaging,
        M3AutomaticAcceptanceFailureMode failureMode,
        DesktopAutomationMode requestedMode)
    {
        IsEnabled = isEnabled;
        MaxMoveCount = maxMoveCount;
        MaxExecutionStepCount = maxExecutionStepCount;
        AllowTemporaryStaging = allowTemporaryStaging;
        FailureMode = failureMode;
        RequestedMode = requestedMode;
    }

    public static M3AutomaticAcceptanceOptions Disabled { get; } =
        new(
            false,
            M3AutomationPoCDefaults.RiskSettings.MaxMoveCount,
            M3AutomationPoCDefaults.RiskSettings.MaxExecutionStepCount,
            M3AutomationPoCDefaults.RiskSettings.AllowTemporaryStaging,
            M3AutomaticAcceptanceFailureMode.None,
            DesktopAutomationMode.Suggestion);

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
        var requestedModeText = lookup(RequestedModeVariable);

        if (mode is null
            && arm is null
            && maxMovesText is null
            && maxStepsText is null
            && allowStagingText is null
            && failureModeText is null
            && requestedModeText is null)
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
        var requestedMode = ParseRequestedMode(requestedModeText);

        return new M3AutomaticAcceptanceOptions(
            true,
            maxMoves,
            maxSteps,
            allowStaging,
            failureMode,
            requestedMode);
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
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)),
            RequestedMode);
    }

    public string Describe() => IsEnabled
        ? $"Mode={SupportedMode}; MaxMoves={MaxMoveCount}; MaxSteps={MaxExecutionStepCount}; "
            + $"AllowStaging={AllowTemporaryStaging}; FailureMode={FailureMode}; "
            + $"RequestedMode={RequestedMode}"
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

    private static DesktopAutomationMode ParseRequestedMode(string? text) =>
        text switch
        {
            null => DesktopAutomationMode.Suggestion,
            AutomaticRequestedModeValue => DesktopAutomationMode.Automatic,
            SuggestionRequestedModeValue => DesktopAutomationMode.Suggestion,
            _ => throw new InvalidOperationException(
                $"{RequestedModeVariable} 若提供，必须精确为 {AutomaticRequestedModeValue}"
                + $" 或 {SuggestionRequestedModeValue}。M3 automatic acceptance 未启用。"),
        };
}
