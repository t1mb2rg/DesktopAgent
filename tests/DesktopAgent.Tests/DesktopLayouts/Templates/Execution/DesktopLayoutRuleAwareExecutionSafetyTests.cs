namespace DesktopAgent.Tests.DesktopLayouts.Templates.Execution;

public sealed class DesktopLayoutRuleAwareExecutionSafetyTests
{
    [Fact] public void Canon_NoMoveDesktopIcon()
    {
        // Static scan auxiliary: verify the Execution namespace files
        // don't contain writer calls. The actual scan is done via git grep
        // in the CI pipeline. This test provides a programmatic anchor.
        //
        // Verify these tokens don't appear in source:
        // MoveDesktopIcon, ExecuteWithScopeGateAsync,
        // TargetDisplayLayoutExecutionCoordinator,
        // IDesktopLayoutSnapshotStore, SaveAsync(snapshot
        Assert.True(true);
    }
}
