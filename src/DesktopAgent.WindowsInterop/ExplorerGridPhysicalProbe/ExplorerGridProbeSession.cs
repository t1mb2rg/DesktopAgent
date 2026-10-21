using System.IO;
using System.Text;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

/// <summary>
/// 一次探测运行的后半程：跑扫描 → 写 Measured 事实 → 排版报告文本 → 落盘日志。
///
/// 诊断工作台与产品校准面板共用这一段，因为这里有两条不许分叉的规矩：
/// ①事实写入失败不改变物理扫描结论，报告如实显示"未写入"及其原因；
/// ②报告落盘失败绝不影响探针结论（界面上始终有完整报告）。
/// </summary>
public sealed class ExplorerGridProbeSession
{
    private readonly ExplorerGridPhysicalProbeRunner _runner;
    private readonly ExplorerGridProbeFactRecorder _recorder;
    private readonly string _logDirectory;
    private readonly TimeProvider _timeProvider;

    public ExplorerGridProbeSession(
        ExplorerGridPhysicalProbeRunner runner,
        ExplorerGridProbeFactRecorder recorder,
        string logDirectory,
        TimeProvider? timeProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new ArgumentException("日志目录不能为空。", nameof(logDirectory));

        _logDirectory = logDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExplorerGridProbeSessionResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = await _runner.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        var recorded = await RecordFactsAsync(_recorder, report, cancellationToken).ConfigureAwait(false);
        var reportText = report.Format() + FormatFactSummary(recorded);
        var reportPath = PersistReport(reportText, _logDirectory, _timeProvider);
        return new ExplorerGridProbeSessionResult(report, recorded, reportText, reportPath);
    }

    /// <summary>
    /// 写入事实。写入失败不改变物理扫描结论：异常转成可读的"未写入"原因，由报告如实呈现。
    /// </summary>
    public static async Task<ExplorerGridProbeFactRecordResult> RecordFactsAsync(
        ExplorerGridProbeFactRecorder recorder,
        ExplorerGridPhysicalProbeReport report,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await recorder.RecordAsync(report, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new ExplorerGridProbeFactRecordResult(
                Array.Empty<ExplorerGridMeasurement>(),
                [$"事实写入失败：{exception.GetType().Name}: {exception.Message}"]);
        }
    }

    public static string FormatFactSummary(ExplorerGridProbeFactRecordResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("=== 网格事实写入 ===");

        if (result.Facts.Count > 0)
            builder.AppendLine($"写入 {result.Facts.Count} 条 Measured 事实（拓扑：{result.Facts[0].TopologyFingerprint}）");

        foreach (var fact in result.Facts)
        {
            builder.AppendLine(
                $"  {fact.DeviceName}: spacing=({fact.Spacing.X},{fact.Spacing.Y}) " +
                $"phase=({fact.Phase.X},{fact.Phase.Y}) samples={fact.SampleCount}");
        }

        foreach (var rejection in result.Rejections)
            builder.AppendLine($"未写入：{rejection}");

        return builder.ToString();
    }

    /// <summary>
    /// 把完整报告落成文件，供无人值守的真机复核方式读取（复核者只需读文件，不必抓 UI 文本）。
    /// 返回 latest 文件路径；落盘失败返回 <c>null</c>。
    /// </summary>
    public static string? PersistReport(
        string text,
        string logDirectory,
        TimeProvider? timeProvider = null)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            var now = (timeProvider ?? TimeProvider.System).GetLocalNow();
            var latest = Path.Combine(logDirectory, "grid-probe-latest.txt");
            File.WriteAllText(latest, text, Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(logDirectory, $"grid-probe-{now:yyyyMMdd-HHmmss}.txt"),
                text,
                Encoding.UTF8);
            return latest;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>产品与诊断共用的报告目录：数据库所在目录下的 logs。</summary>
    public static string LogDirectoryFor(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));

        var directory = Path.GetDirectoryName(databasePath);
        return Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            "logs");
    }
}

/// <summary>一次探测运行的完整结果：报告、事实写入结果、排版好的报告文本与落盘位置。</summary>
public sealed record ExplorerGridProbeSessionResult(
    ExplorerGridPhysicalProbeReport Report,
    ExplorerGridProbeFactRecordResult Recorded,
    string ReportText,
    string? ReportPath)
{
    public ExplorerGridProbeRunStatus Status => Report.Status;

    public int MeasuredDisplayCount => Report.Displays.Count(display => display.IsMeasured);
}
