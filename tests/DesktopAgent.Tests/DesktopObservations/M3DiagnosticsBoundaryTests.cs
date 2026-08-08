using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Poc;
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
    public void MonitoringSession_DependsOnReadOnlyDiagnosticsAndHintSettings()
    {
        var parameterTypes = typeof(M3ReadOnlyMonitoringSession)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(DesktopObservationDiagnosticsRunner),
                typeof(DesktopObservationHintBatchSettings),
            },
            parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void DiagnosticsWindow_DependsOnDiagnosticsRunnerAndReadOnlyMonitoringSession()
    {
        var parameterTypes = typeof(M3DiagnosticsWindow)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(DesktopObservationDiagnosticsRunner),
                typeof(M3ReadOnlyMonitoringSession),
            },
            parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }
}
