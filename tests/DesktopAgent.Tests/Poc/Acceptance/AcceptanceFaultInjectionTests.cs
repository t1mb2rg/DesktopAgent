using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc.Acceptance;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.Poc.Acceptance;

public sealed class AcceptanceFaultInjectionTests
{
    [Fact]
    public void FromLookup_NoVariables_IsDisabled()
    {
        var options = AcceptanceFaultInjectionOptions.FromLookup(_ => null);

        Assert.False(options.IsEnabled);
        Assert.Equal("Disabled", options.Describe());
    }

    [Fact]
    public void FromLookup_PartialConfiguration_FailsClosed()
    {
        var values = new Dictionary<string, string?>
        {
            [AcceptanceFaultInjectionOptions.ModeVariable] = AcceptanceFaultInjectionOptions.SupportedMode,
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcceptanceFaultInjectionOptions.FromLookup(name =>
                values.TryGetValue(name, out var value) ? value : null));

        Assert.Contains(AcceptanceFaultInjectionOptions.MoveCallVariable, ex.Message);
    }

    [Fact]
    public void FromLookup_WrongArmValue_FailsClosed()
    {
        var values = ValidValues();
        values[AcceptanceFaultInjectionOptions.ArmVariable] = "no";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcceptanceFaultInjectionOptions.FromLookup(name => values[name]));

        Assert.Contains(AcceptanceFaultInjectionOptions.ArmVariable, ex.Message);
    }

    [Fact]
    public void FromLookup_CompleteConfiguration_EnablesExactTarget()
    {
        var values = ValidValues();

        var options = AcceptanceFaultInjectionOptions.FromLookup(name => values[name]);

        Assert.True(options.IsEnabled);
        Assert.Equal(AcceptanceFaultInjectionBehavior.HardCrash, options.Behavior);
        Assert.True(options.WillTerminateProcess);
        Assert.Equal(2, options.MoveCallIndex);
        Assert.Equal("c:\\desktop\\m2-a.txt", options.StableId);
    }

    [Fact]
    public void FromLookup_OmittedBehavior_PreservesHardCrashCompatibility()
    {
        var values = ValidValues();
        values.Remove(AcceptanceFaultInjectionOptions.BehaviorVariable);

        var options = AcceptanceFaultInjectionOptions.FromLookup(name =>
            values.TryGetValue(name, out var value) ? value : null);

        Assert.Equal(AcceptanceFaultInjectionBehavior.HardCrash, options.Behavior);
        Assert.True(options.WillTerminateProcess);
    }

    [Fact]
    public void FromLookup_SoftError_RequiresSoftArmAndDoesNotTerminate()
    {
        var values = ValidValues();
        values[AcceptanceFaultInjectionOptions.BehaviorVariable] =
            AcceptanceFaultInjectionOptions.SoftErrorBehaviorValue;
        values[AcceptanceFaultInjectionOptions.ArmVariable] =
            AcceptanceFaultInjectionOptions.RequiredSoftErrorArmValue;

        var options = AcceptanceFaultInjectionOptions.FromLookup(name => values[name]);

        Assert.True(options.IsEnabled);
        Assert.Equal(AcceptanceFaultInjectionBehavior.SoftError, options.Behavior);
        Assert.False(options.WillTerminateProcess);
    }

    [Fact]
    public void FromLookup_SoftErrorWithHardCrashArm_FailsClosed()
    {
        var values = ValidValues();
        values[AcceptanceFaultInjectionOptions.BehaviorVariable] =
            AcceptanceFaultInjectionOptions.SoftErrorBehaviorValue;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcceptanceFaultInjectionOptions.FromLookup(name => values[name]));

        Assert.Contains(AcceptanceFaultInjectionOptions.ArmVariable, ex.Message);
    }

    [Fact]
    public void MoveDesktopIcon_ExactConfiguredCall_InvokesTerminatorAfterInnerWrite()
    {
        var options = EnabledOptions(callIndex: 1, stableId: "icon-a");
        var inner = new RecordingWriter(isExact: true);
        string? terminationMessage = null;
        var writer = new AcceptanceFaultInjectingDesktopIconPositionWriter(
            inner,
            options,
            message => terminationMessage = message);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            writer.MoveDesktopIcon("icon-a", new PixelPoint(100, 200)));

        Assert.Equal(1, inner.MoveCallCount);
        Assert.NotNull(terminationMessage);
        Assert.Contains("before caller checkpoint", terminationMessage);
        Assert.Contains("Behavior=HardCrash", terminationMessage);
        Assert.Contains("终止器意外返回", ex.Message);
    }

    [Fact]
    public void MoveDesktopIcon_SoftError_ThrowsWithoutTerminatingAndSchedulesUiReturn()
    {
        var options = EnabledOptions(
            callIndex: 1,
            stableId: "icon-a",
            AcceptanceFaultInjectionBehavior.SoftError);
        var inner = new RecordingWriter(isExact: true);
        var terminated = false;
        string? scheduledMessage = null;
        var writer = new AcceptanceFaultInjectingDesktopIconPositionWriter(
            inner,
            options,
            _ => terminated = true,
            message => scheduledMessage = message);

        var ex = Assert.Throws<AcceptanceInjectedSoftFaultException>(() =>
            writer.MoveDesktopIcon("icon-a", new PixelPoint(100, 200)));

        Assert.Equal(1, inner.MoveCallCount);
        Assert.False(terminated);
        Assert.NotNull(scheduledMessage);
        Assert.Contains("Behavior=SoftError", scheduledMessage);
        Assert.Contains("before caller checkpoint", ex.Message);
    }

    [Fact]
    public void MoveDesktopIcon_WrongStableId_DoesNotTerminate()
    {
        var options = EnabledOptions(callIndex: 1, stableId: "icon-a");
        var inner = new RecordingWriter(isExact: true);
        var terminated = false;
        var writer = new AcceptanceFaultInjectingDesktopIconPositionWriter(
            inner,
            options,
            _ => terminated = true);

        var result = writer.MoveDesktopIcon("icon-b", new PixelPoint(100, 200));

        Assert.True(result.IsExact);
        Assert.False(terminated);
    }

    [Fact]
    public void MoveDesktopIcon_MatchingButInexact_FailsClosedWithoutTerminating()
    {
        var options = EnabledOptions(callIndex: 1, stableId: "icon-a");
        var inner = new RecordingWriter(isExact: false);
        var terminated = false;
        var writer = new AcceptanceFaultInjectingDesktopIconPositionWriter(
            inner,
            options,
            _ => terminated = true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            writer.MoveDesktopIcon("icon-a", new PixelPoint(100, 200)));

        Assert.False(terminated);
        Assert.Contains("未报告 ExactSuccess", ex.Message);
    }

    private static Dictionary<string, string?> ValidValues() => new()
    {
        [AcceptanceFaultInjectionOptions.ModeVariable] = AcceptanceFaultInjectionOptions.SupportedMode,
        [AcceptanceFaultInjectionOptions.BehaviorVariable] = AcceptanceFaultInjectionOptions.HardCrashBehaviorValue,
        [AcceptanceFaultInjectionOptions.MoveCallVariable] = "2",
        [AcceptanceFaultInjectionOptions.StableIdVariable] = "c:\\desktop\\m2-a.txt",
        [AcceptanceFaultInjectionOptions.ArmVariable] = AcceptanceFaultInjectionOptions.RequiredHardCrashArmValue,
    };

    private static AcceptanceFaultInjectionOptions EnabledOptions(
        int callIndex,
        string stableId,
        AcceptanceFaultInjectionBehavior behavior = AcceptanceFaultInjectionBehavior.HardCrash)
    {
        var values = new Dictionary<string, string?>
        {
            [AcceptanceFaultInjectionOptions.ModeVariable] = AcceptanceFaultInjectionOptions.SupportedMode,
            [AcceptanceFaultInjectionOptions.BehaviorVariable] = behavior == AcceptanceFaultInjectionBehavior.HardCrash
                ? AcceptanceFaultInjectionOptions.HardCrashBehaviorValue
                : AcceptanceFaultInjectionOptions.SoftErrorBehaviorValue,
            [AcceptanceFaultInjectionOptions.MoveCallVariable] = callIndex.ToString(),
            [AcceptanceFaultInjectionOptions.StableIdVariable] = stableId,
            [AcceptanceFaultInjectionOptions.ArmVariable] = behavior == AcceptanceFaultInjectionBehavior.HardCrash
                ? AcceptanceFaultInjectionOptions.RequiredHardCrashArmValue
                : AcceptanceFaultInjectionOptions.RequiredSoftErrorArmValue,
        };

        return AcceptanceFaultInjectionOptions.FromLookup(name => values[name]);
    }

    private sealed class RecordingWriter(bool isExact) : IDesktopIconPositionWriter
    {
        public int MoveCallCount { get; private set; }

        public DesktopIconPositioningCapabilities GetCapabilities() =>
            throw new NotSupportedException();

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
        {
            MoveCallCount++;
            var actual = isExact
                ? targetPosition
                : new PixelPoint(targetPosition.X + 1, targetPosition.Y);

            return new DesktopIconMoveResult(
                stableId,
                new PixelPoint(10, 20),
                targetPosition,
                actual,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }
}
