using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;

public sealed record DesktopGridCalibrationResult(bool IsReady, string Message, string? TopologyIdentity);

/// <summary>产品的统一校准门。实测事实不等于执行授权；推断几何永远不能放行。</summary>
public sealed class DesktopGridCalibration
{
    private readonly DesktopExplorerGridFactReader _reader;
    private readonly Func<DesktopObservation, CancellationToken, Task>? _observeEnvironment;
    private long _requiredAfterUtcTicks;

    public DesktopGridCalibration(DesktopExplorerGridFactReader reader,
        Func<DesktopObservation, CancellationToken, Task>? observeEnvironment = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _observeEnvironment = observeEnvironment;
    }

    public void RequireMeasurementsAfter(DateTimeOffset value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _requiredAfterUtcTicks);
            if (current >= value.UtcTicks) return;
        } while (Interlocked.CompareExchange(ref _requiredAfterUtcTicks, value.UtcTicks, current) != current);
    }

    public async Task<DesktopGridCalibrationResult> CheckAsync(
        DesktopObservation observation, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_observeEnvironment is not null)
                await _observeEnvironment(observation, cancellationToken).ConfigureAwait(false);
            var facts = await _reader.TryReadActiveAsync(observation, cancellationToken).ConfigureAwait(false);
            return Evaluate(observation, facts?.Where(fact =>
                fact.ObservedAtUtc.UtcTicks > Interlocked.Read(ref _requiredAfterUtcTicks)).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(false, $"无法核验校准事实：{ex.Message}。请重试，不能使用推断网格。", null);
        }
    }

    public static DesktopGridCalibrationResult Evaluate(
        DesktopObservation observation, IReadOnlyList<ExplorerGridMeasurement>? facts)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var identity = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        if (identity is null)
            return new(false, "无法读取完整显示环境，请检查显示器连接后重试。", null);

        var missing = observation.Displays.Where(display => !(facts ?? []).Any(fact =>
            fact.DeviceName == display.DeviceName && fact.CanAuthorizeExecution(identity) &&
            fact.Spacing == observation.PositioningCapabilities.Spacing))
            .Select(display => display.DeviceName).ToArray();
        if (missing.Length > 0)
            return new(false, $"请先校准全部显示器。缺少当前环境的实测网格：{string.Join("、", missing)}。", identity);

        var geometry = DesktopExplorerWorkspaceGeometryAdapter.Build(observation, facts);
        if (!geometry.Succeeded || geometry.Provenance.Count != observation.Displays.Count ||
            geometry.Provenance.Any(entry => entry.Source != ExplorerGridEvidenceSource.Measured))
            return new(false, "实测网格未能建立可靠几何，请重新校准。" +
                string.Join("；", geometry.Issues.Select(issue => issue.Message)), identity);

        return new(true, $"{observation.Displays.Count} 块显示器的实测网格有效。", identity);
    }
}

/// <summary>给产品计算与执行链使用；物理校准和已有恢复现场继续使用原始只读观测。</summary>
public sealed class CalibratedDesktopObservationSource : IDesktopObservationSource
{
    private readonly IDesktopObservationSource _source;
    private readonly DesktopGridCalibration? _calibration;

    public CalibratedDesktopObservationSource(IDesktopObservationSource source, DesktopGridCalibration? calibration)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _calibration = calibration;
    }

    public async Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
    {
        var capture = await _source.CaptureAsync(ct).ConfigureAwait(false);
        if (!capture.IsCaptured)
            return capture;
        var result = _calibration is null
            ? new DesktopGridCalibrationResult(false, "产品校准验证器未配置，禁止继续执行。", null)
            : await _calibration.CheckAsync(capture.Observation!, ct).ConfigureAwait(false);
        return result.IsReady ? capture : DesktopObservationCaptureResult.Unavailable(
            new DesktopObservationCaptureIssue(DesktopObservationCaptureIssueCode.CalibrationRequired, result.Message));
    }
}
