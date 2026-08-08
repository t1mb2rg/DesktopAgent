using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Poc;
using DesktopAgent.Poc.Hosting;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class M3DiagnosticsBoundaryTests
{
    [Fact]
    public void DiagnosticsRunner_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(DesktopObservationDiagnosticsRunner)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IStableDesktopObservationSource), parameterTypes);
        Assert.Contains(typeof(DesktopStateBaselineTracker), parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void MonitoringRuntime_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(DesktopObservationMonitoringRuntime)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(DesktopObservationBackgroundPump), parameterTypes);
        Assert.Contains(typeof(DesktopStateBaselineTracker), parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void DiagnosticsWindow_DependsOnlyOnMonitoringRuntime()
    {
        var parameters = typeof(M3DiagnosticsWindow)
            .GetConstructors()
            .Single()
            .GetParameters();

        var parameter = Assert.Single(parameters);
        Assert.Equal(typeof(DesktopObservationMonitoringRuntime), parameter.ParameterType);
        Assert.DoesNotContain(
            parameters,
            item => item.ParameterType == typeof(IDesktopIconPositionWriter));
    }

    [Fact]
    public void HostAdapter_DoesNotExposeWriterDependency()
    {
        var parameters = typeof(M3ObservationMonitoringHostedService)
            .GetConstructors()
            .Single()
            .GetParameters();

        var parameter = Assert.Single(parameters);
        Assert.Equal(typeof(DesktopObservationMonitoringRuntime), parameter.ParameterType);
        Assert.DoesNotContain(
            parameters,
            item => item.ParameterType == typeof(IDesktopIconPositionWriter));
    }
}
