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
    public void MonitoringSession_DependsOnlyOnReadOnlyMonitoringComponents()
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
                typeof(IM3ReadOnlyMonitoringHintSourceFactory),
                typeof(IPeriodicObservationHintSource),
            },
            parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void DiagnosticsWindow_HasNotificationCompositionConstructorWithoutWriter()
    {
        var constructors = typeof(M3DiagnosticsWindow).GetConstructors();
        Assert.Equal(2, constructors.Length);

        var compositionConstructor = Assert.Single(
            constructors.Where(constructor => constructor.GetParameters().Length == 2));
        var parameterTypes = compositionConstructor
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(M3SuggestionMonitoringCoordinator),
                typeof(M3SuggestionNotificationCoordinator),
            },
            parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void SuggestionCoordinator_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(M3SuggestionMonitoringCoordinator)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void NotificationCoordinator_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(M3SuggestionNotificationCoordinator)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void TrayController_DependsOnPresentationAndReadOnlyM3Coordinators_NotWriter()
    {
        var parameterTypes = typeof(M3TrayController)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(MainWindow),
                typeof(M3DiagnosticsWindow),
                typeof(M3SuggestionMonitoringCoordinator),
                typeof(M3SuggestionNotificationCoordinator),
            },
            parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }
}
