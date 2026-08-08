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
    public void DiagnosticsWindow_DependsOnlyOnDiagnosticsRunner()
    {
        var parameters = typeof(M3DiagnosticsWindow)
            .GetConstructors()
            .Single()
            .GetParameters();

        var parameter = Assert.Single(parameters);
        Assert.Equal(typeof(DesktopObservationDiagnosticsRunner), parameter.ParameterType);
        Assert.DoesNotContain(
            parameters,
            item => item.ParameterType == typeof(IDesktopIconPositionWriter));
    }
}
