using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.WinUI;

internal static class ProductGridCalibration
{
    private static readonly JsonGridCalibrationEnvironmentStore EnvironmentStore = new(Path.Combine(
        Path.GetDirectoryName(DesktopAgentDataPaths.DefaultDatabasePath)!, "grid-calibration-environment.json"));

    internal static DesktopGridCalibration Calibration { get; } = new(
        new DesktopExplorerGridFactReader(new SqliteExplorerGridMeasurementStore(DesktopAgentDataPaths.DefaultDatabasePath)),
        ObserveEnvironmentAsync);

    internal static IDesktopObservationSource Protect(IDesktopObservationSource source)
        => new CalibratedDesktopObservationSource(source, Calibration);

    private static async Task ObserveEnvironmentAsync(DesktopObservation observation, CancellationToken ct)
    {
        var required = await EnvironmentStore.ObserveAsync(ExplorerGridTopologyIdentity.TryComputeFor(observation), ct);
        if (required is { } value) Calibration.RequireMeasurementsAfter(value);
    }

    internal static async Task InvalidateAsync()
    {
        // 先锁住内存状态；持久化失败也不得继续沿用旧事实。
        Calibration.RequireMeasurementsAfter(DateTimeOffset.UtcNow);
        var required = await EnvironmentStore.InvalidateAsync();
        if (required is { } value) Calibration.RequireMeasurementsAfter(value);
    }
}
