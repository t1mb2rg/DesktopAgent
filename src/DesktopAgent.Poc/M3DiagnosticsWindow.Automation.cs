using System.Text;
using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.WindowsInterop.DesktopAutomation;

namespace DesktopAgent.Poc;

public partial class M3DiagnosticsWindow
{
    /// <summary>
    /// 把 M3-B05 自动执行周期的结果写进诊断日志。
    /// 周期可能在 preflight / fresh handoff / 自动安全门的任意一段原地停止，而托盘只显示一行状态；
    /// 无人值守时，只有诊断日志能回答“为什么没有动手、卡在哪一段、风险预算是多少”。
    /// </summary>
    public void AppendAutomaticCycleResult(DesktopAutomationExecutionCycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        AppendDiagnostics(FormatAutomaticCycleResult(result));
    }

    /// <summary>
    /// 周期结果的一行文本（写日志与测试共用，保证两边看的是同一份事实）。
    /// </summary>
    internal static string FormatAutomaticCycleResult(DesktopAutomationExecutionCycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var runtime = result.RuntimeAtStart;
        var builder = new StringBuilder();
        builder.Append("[B05] automatic cycle ").Append(result.Status);
        builder.Append(" | mode=").Append(runtime.RequestedMode)
            .Append('/').Append(runtime.EffectiveMode)
            .Append(" gate=").Append(runtime.UnattendedGate)
            .Append(" paused=").Append(runtime.IsPaused ? "true" : "false")
            .Append(" cooldown=").Append(runtime.IsCooldownActive ? "true" : "false")
            .Append(" fuse=").Append(runtime.IsFuseTripped ? "true" : "false");

        if (runtime.PauseReason is { Length: > 0 } pauseReason)
            builder.Append(" pauseReason=").Append(pauseReason);

        if (result.LifecycleAtStart.CurrentSuggestion is { } suggestion)
        {
            builder.Append(" | suggestion=").Append(suggestion.SuggestionId)
                .Append(" lifecycleRev=").Append(result.LifecycleAtStart.Revision);
        }

        if (result.Preflight is { } preflight)
        {
            builder.Append(" | preflight=").Append(preflight.Status);

            if (preflight.RiskAssessment is { } risk)
            {
                // 风险预算：候选要动多少、允许动多少。预算不通过时这里就是“为什么没动手”的答案。
                builder.Append(" riskMoves=").Append(risk.Evidence.MoveRequiredCount)
                    .Append('/').Append(risk.Settings.MaxMoveCount)
                    .Append(" riskSteps=").Append(risk.Evidence.ExecutionStepCount)
                    .Append('/').Append(risk.Settings.MaxExecutionStepCount)
                    .Append(" staging=").Append(risk.Evidence.UsesTemporaryStaging ? "true" : "false")
                    .Append(" withinBudget=").Append(risk.WithinAutomationRiskBudget ? "true" : "false");

                if (risk.BlockReasons.Count > 0)
                    builder.Append(" blockReasons=").Append(string.Join(',', risk.BlockReasons));
            }
        }

        if (result.Handoff is { } handoff)
            builder.Append(" | handoff=").Append(handoff.Status);

        if (result.SafetyGate is { } safetyGate)
            builder.Append(" | safetyGate=").Append(safetyGate.Status);

        if (result.Outcome is { } outcome)
            builder.Append(" | outcome=").Append(outcome.Status);

        if (!string.IsNullOrWhiteSpace(result.Fault))
            builder.Append(" | fault=").Append(result.Fault);

        return builder.ToString();
    }
}
