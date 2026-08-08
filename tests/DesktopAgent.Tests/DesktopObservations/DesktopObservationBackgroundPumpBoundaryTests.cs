using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopObservationBackgroundPumpBoundaryTests
{
    [Fact]
    public void Constructor_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(DesktopObservationBackgroundPump)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public void Processor_Constructor_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(DesktopObservationBatchProcessor)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }
}
