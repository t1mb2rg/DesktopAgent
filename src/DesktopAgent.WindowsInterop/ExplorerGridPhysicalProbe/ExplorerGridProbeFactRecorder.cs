using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

/// <summary>
/// Physical Gate 通过后，把每块显示器的网格事实逐屏写入 store。
///
/// 事实是逐屏的：<see cref="ExplorerGridProbeRunStatus.Passed"/> 与
/// <see cref="ExplorerGridProbeRunStatus.Partial"/> 都写入已实测屏的 Measured 事实，
/// 未实测屏只记录原因。达不到门槛的显示器不写入，也不在这里降级成推断事实：
/// 降级属于策略层的决定，Diagnostics 只负责记录"测到了什么"和"为什么没测到"。
/// </summary>
public sealed class ExplorerGridProbeFactRecorder
{
    private readonly IExplorerGridMeasurementStore _store;

    public ExplorerGridProbeFactRecorder(IExplorerGridMeasurementStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<ExplorerGridProbeFactRecordResult> RecordAsync(
        ExplorerGridPhysicalProbeReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Diagnostics 是事实的唯一写入方；写入前确保表存在，不依赖产品路径的初始化时机。
        await _store.InitializeAsync(cancellationToken);

        var result = BuildFacts(report);
        foreach (var fact in result.Facts)
            await _store.AppendObservationAsync(fact, cancellationToken);

        return result;
    }

    public static ExplorerGridProbeFactRecordResult BuildFacts(ExplorerGridPhysicalProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var facts = new List<ExplorerGridMeasurement>();
        var rejections = new List<string>();

        if (report.Status is not (ExplorerGridProbeRunStatus.Passed or ExplorerGridProbeRunStatus.Partial))
        {
            return Result(facts, [$"Physical Gate 未通过（Status: {report.Status}），不写入任何网格事实。"]);
        }

        if (string.IsNullOrWhiteSpace(report.TopologyIdentity))
        {
            return Result(facts, ["报告没有拓扑身份，事实无法绑定环境，不写入。"]);
        }

        if (report.Displays.Count == 0)
        {
            return Result(facts, ["报告没有显示器结果，不写入事实。"]);
        }

        if (!report.SharedSpacing)
            return Result(facts, ["共享 spacing 未通过，不写入事实。"]);

        // 相位是逐屏校验的（每块屏的相位锚定在它自己的工作区原点），不再要求跨屏相位相同。
        if (!report.PhaseVerifiedPerDisplay)
            return Result(facts, ["逐屏相位校验未通过，不写入事实。"]);

        foreach (var display in report.Displays)
        {
            if (!display.IsMeasured)
            {
                rejections.Add(
                    $"{display.DeviceName}: 未完成实测（{display.UnmeasuredReason ?? "原因未记录"}），不写入事实。");
                continue;
            }

            if (!display.ObservedSpacingConstant || !display.ObservedPhaseConstant)
            {
                rejections.Add(
                    $"{display.DeviceName}: observed spacing/phase 恒定标志未通过，不写入事实。");
                continue;
            }

            // 被动占用样本只是佐证，不能凑够 Physical Gate 的主动落点样本数。
            if (display.ActiveHorizontalSampleCount < ExplorerGridMeasurement.MinimumMeasuredActiveSamplesPerAxis ||
                display.ActiveVerticalSampleCount < ExplorerGridMeasurement.MinimumMeasuredActiveSamplesPerAxis)
            {
                rejections.Add(
                    $"{display.DeviceName}: 主动落点样本不足（horizontal={display.ActiveHorizontalSampleCount} " +
                    $"vertical={display.ActiveVerticalSampleCount}，每轴至少 " +
                    $"{ExplorerGridMeasurement.MinimumMeasuredActiveSamplesPerAxis}），不写入事实。");
                continue;
            }

            facts.Add(new ExplorerGridMeasurement(
                ExplorerGridEvidenceSource.Measured,
                display.DeviceName,
                display.Spacing,
                display.Phase,
                display.ActiveScanSampleCount,
                display.ActiveScanSampleCount,
                spacingConstant: true,
                phaseConstant: true,
                report.TopologyIdentity,
                report.FinishedAtUtc));
        }

        return Result(facts, rejections);
    }

    private static ExplorerGridProbeFactRecordResult Result(
        List<ExplorerGridMeasurement> facts,
        IReadOnlyList<string> rejections) =>
        new(Array.AsReadOnly(facts.ToArray()), rejections);
}

public sealed record ExplorerGridProbeFactRecordResult(
    IReadOnlyList<ExplorerGridMeasurement> Facts,
    IReadOnlyList<string> Rejections);
