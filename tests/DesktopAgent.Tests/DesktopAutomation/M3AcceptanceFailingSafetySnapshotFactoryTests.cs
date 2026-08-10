using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Poc.Acceptance;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class M3AcceptanceFailingSafetySnapshotFactoryTests
{
    [Fact]
    public void Create_AlwaysFailsBeforeProducingSnapshot()
    {
        var factory = new M3AcceptanceFailingSafetySnapshotFactory();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(
                Array.Empty<DesktopIconDescriptor>(),
                @"\\.\DISPLAY1",
                default));

        Assert.Equal(
            M3AcceptanceFailingSafetySnapshotFactory.InjectedFailureMessage,
            ex.Message);
    }
}
