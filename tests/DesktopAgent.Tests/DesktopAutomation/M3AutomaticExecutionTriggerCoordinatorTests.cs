using System.Reflection;
using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Poc;
using DesktopAgent.Tests.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopAutomation;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class M3AutomaticExecutionTriggerCoordinatorTests
{
    [Fact]
    public async Task NoActiveSuggestion_StartDoesNotRunCycle()
    {
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var runtime = RuntimePolicy();
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        var calls = 0;
        using var trigger = Trigger(
            lifecycle,
            runtime,
            _ =>
            {
                calls++;
                return Task.FromResult(FakeCycle(runtime.Snapshot, lifecycle.Snapshot));
            });

        trigger.Start();
        await Task.Delay(50);

        Assert.Equal(0, calls);
        Assert.False(trigger.IsCyclePumpRunning);
    }

    [Fact]
    public async Task SameActiveLifecycleAndRuntimeKey_IsAttemptedOnlyOnce()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var runtime = RuntimePolicy();
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        var calls = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trigger = Trigger(
            lifecycle,
            runtime,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    completed.TrySetResult();
                return Task.FromResult(FakeCycle(runtime.Snapshot, lifecycle.Snapshot));
            });

        trigger.Start();
        lifecycle.Publish(suggestion);
        for (var i = 0; i < 10; i++)
            trigger.RequestCurrent();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await trigger.StopAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NewLifecycleRevision_AllowsAnotherAttempt()
    {
        var s1 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var s2 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var runtime = RuntimePolicy();
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trigger = Trigger(
            lifecycle,
            runtime,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                    second.TrySetResult();
                return Task.FromResult(FakeCycle(runtime.Snapshot, lifecycle.Snapshot));
            });

        trigger.Start();
        lifecycle.Publish(s1);
        trigger.RequestCurrent();
        await WaitForAsync(() => Volatile.Read(ref calls) == 1);

        lifecycle.Publish(s2);
        trigger.RequestCurrent();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await trigger.StopAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CooldownNaturalExpiry_ChangesTriggerKeyWithoutRuntimeRevision()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var runtime = new DesktopAutomationRuntimePolicy(
            new DesktopAutomationRuntimePolicySettings(TimeSpan.FromMinutes(5), 3),
            DesktopAutomationUnattendedGate.ValidatedOpen,
            clock);
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        runtime.RecordExecutionFailure();
        var revisionDuringCooldown = runtime.Snapshot.Revision;
        Assert.True(runtime.Snapshot.IsCooldownActive);

        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trigger = Trigger(
            lifecycle,
            runtime,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                    second.TrySetResult();
                return Task.FromResult(FakeCycle(runtime.Snapshot, lifecycle.Snapshot));
            });

        trigger.Start();
        await WaitForAsync(() => Volatile.Read(ref calls) == 1);
        Assert.Equal(revisionDuringCooldown, runtime.Snapshot.Revision);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(runtime.Snapshot.IsCooldownActive);
        Assert.Equal(revisionDuringCooldown, runtime.Snapshot.Revision);

        trigger.RequestCurrent();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await trigger.StopAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NewerSuggestionArrivingDuringCycle_IsCoalescedAndRunsAfterFirst()
    {
        var s1 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var s2 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var runtime = RuntimePolicy();
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var trigger = Trigger(
            lifecycle,
            runtime,
            async _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                }
                else if (call == 2)
                {
                    secondCompleted.TrySetResult();
                }
                return FakeCycle(runtime.Snapshot, lifecycle.Snapshot);
            });

        try
        {
            trigger.Start();
            lifecycle.Publish(s1);
            trigger.RequestCurrent();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            lifecycle.Publish(s2);
            trigger.RequestCurrent();
            trigger.RequestCurrent();
            releaseFirst.TrySetResult();

            await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await trigger.StopAsync();

            Assert.Equal(2, calls);
        }
        finally
        {
            // 任一等待或断言先失败时，也必须先放行 fake cycle；
            // 否则 using Dispose -> StopAsync 会反向等待这个永远无人释放的 Task。
            releaseFirst.TrySetResult();
            await trigger.StopAsync();
        }
    }

    [Fact]
    public async Task StopAsync_DrainsInFlightCycleAndDropsPendingWork()
    {
        var s1 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var s2 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var runtime = RuntimePolicy();
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var trigger = Trigger(
            lifecycle,
            runtime,
            async _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task;
                return FakeCycle(runtime.Snapshot, lifecycle.Snapshot);
            });

        try
        {
            trigger.Start();
            lifecycle.Publish(s1);
            trigger.RequestCurrent();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            lifecycle.Publish(s2);
            trigger.RequestCurrent();
            var stop = trigger.StopAsync();
            await Task.Delay(50);
            Assert.False(stop.IsCompleted);

            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, calls);
            Assert.False(trigger.IsStarted);
            Assert.False(trigger.IsCyclePumpRunning);
        }
        finally
        {
            // 保证测试失败路径不会把 StopAsync 的 drain 语义放大成 testhost 永久挂起。
            release.TrySetResult();
            await trigger.StopAsync();
        }
    }

    [Fact]
    public async Task RequestCurrentIfAutomatic_IgnoresSuggestionModeAndPokesOnlyOnceInAutomatic()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var runtime = RuntimePolicy();
        var calls = 0;
        using var trigger = Trigger(
            lifecycle,
            runtime,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(FakeCycle(runtime.Snapshot, lifecycle.Snapshot));
            });

        trigger.Start();
        lifecycle.Publish(suggestion);

        // Suggestion 模式：等价于“没有人去托盘把模式切到 Automatic”，点火输入必须被忽略。
        runtime.SetRequestedMode(DesktopAutomationMode.Suggestion);
        trigger.RequestCurrentIfAutomatic();
        await Task.Delay(50);
        Assert.Equal(0, calls);

        // 以 Automatic 启动（没人点托盘）时，周期性点火输入要能拉起一次 cycle。
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        trigger.RequestCurrentIfAutomatic();
        await WaitForAsync(() => Volatile.Read(ref calls) == 1);

        // 同一份建议重复点火不会重复执行。
        trigger.RequestCurrentIfAutomatic();
        trigger.RequestCurrentIfAutomatic();
        await Task.Delay(50);
        await trigger.StopAsync();

        Assert.Equal(1, calls);
    }

    private static M3AutomaticExecutionTriggerCoordinator Trigger(
        DesktopLayoutSuggestionLifecycle lifecycle,
        DesktopAutomationRuntimePolicy runtime,
        Func<CancellationToken, Task<DesktopAutomationExecutionCycleResult>> runCycle)
    {
        var instance = Activator.CreateInstance(
            typeof(M3AutomaticExecutionTriggerCoordinator),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[]
            {
                (Func<DesktopLayoutSuggestionLifecycleSnapshot>)(() => lifecycle.Snapshot),
                (Func<DesktopAutomationRuntimeSnapshot>)(() => runtime.Snapshot),
                runCycle,
            },
            culture: null);

        return Assert.IsType<M3AutomaticExecutionTriggerCoordinator>(instance);
    }

    private static DesktopAutomationRuntimePolicy RuntimePolicy() =>
        new(
            new DesktopAutomationRuntimePolicySettings(TimeSpan.Zero, 3),
            DesktopAutomationUnattendedGate.ValidatedOpen);

    private static DesktopAutomationExecutionCycleResult FakeCycle(
        DesktopAutomationRuntimeSnapshot runtime,
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle) =>
        new(
            DesktopAutomationExecutionCycleStatus.CycleAlreadyRunning,
            runtime,
            lifecycle,
            runtime);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待测试条件超时。");
            await Task.Delay(10);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
