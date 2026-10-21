using System.Reflection;
using DesktopAgent.Poc;

namespace DesktopAgent.Tests.Poc;

public sealed class M3TrayWindowLifecycleStateTests
{
    [Fact]
    public void FreshState_AllowsTrayOpen()
    {
        var state = new StateHarness();

        Assert.True(state.CanOpenFromTray);
        Assert.False(state.IsExitRequested);
        Assert.False(state.IsMainWindowClosed);
        Assert.False(state.CanShowMainWindowForExit);
    }

    [Fact]
    public void BeginExit_BlocksConcurrentTrayOpen_ButAllowsExitToRevealLiveMainWindow()
    {
        var state = new StateHarness();

        Assert.True(state.TryBeginExit());

        Assert.True(state.IsExitRequested);
        Assert.False(state.CanOpenFromTray);
        Assert.True(state.CanShowMainWindowForExit);
        Assert.False(state.TryBeginExit());
    }

    [Fact]
    public void CancelExit_BeforeWindowClosed_RestoresTrayOpenEligibility()
    {
        var state = new StateHarness();
        Assert.True(state.TryBeginExit());

        state.CancelExit();

        Assert.False(state.IsExitRequested);
        Assert.False(state.IsMainWindowClosed);
        Assert.True(state.CanOpenFromTray);
    }

    [Fact]
    public void MainWindowClosed_IsTerminalAndCannotBeShownAgain()
    {
        var state = new StateHarness();

        state.MarkMainWindowClosed();

        Assert.True(state.IsMainWindowClosed);
        Assert.False(state.CanOpenFromTray);
        Assert.False(state.CanShowMainWindowForExit);
        Assert.False(state.TryBeginExit());
    }

    [Fact]
    public void CancelExit_AfterWindowClosed_DoesNotResurrectTrayOpenEligibility()
    {
        var state = new StateHarness();
        Assert.True(state.TryBeginExit());
        state.MarkMainWindowClosed();

        state.CancelExit();

        Assert.True(state.IsExitRequested);
        Assert.True(state.IsMainWindowClosed);
        Assert.False(state.CanOpenFromTray);
        Assert.False(state.CanShowMainWindowForExit);
    }

    private sealed class StateHarness
    {
        private static readonly Type StateType =
            typeof(M3TrayController).Assembly.GetType(
                "DesktopAgent.Poc.M3TrayWindowLifecycleState",
                throwOnError: true)!;

        private readonly object _instance = Activator.CreateInstance(StateType, nonPublic: true)!;

        public bool IsExitRequested => ReadBool(nameof(IsExitRequested));

        public bool IsMainWindowClosed => ReadBool(nameof(IsMainWindowClosed));

        public bool CanOpenFromTray => ReadBool(nameof(CanOpenFromTray));

        public bool CanShowMainWindowForExit => ReadBool(nameof(CanShowMainWindowForExit));

        public bool TryBeginExit() => (bool)Invoke(nameof(TryBeginExit))!;

        public void CancelExit() => Invoke(nameof(CancelExit));

        public void MarkMainWindowClosed() => Invoke(nameof(MarkMainWindowClosed));

        private bool ReadBool(string name) =>
            (bool)(StateType.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(_instance)
                ?? throw new MissingMemberException(StateType.FullName, name));

        private object? Invoke(string name)
        {
            var method = StateType.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMethodException(StateType.FullName, name);

            return method.Invoke(_instance, null);
        }
    }
}
