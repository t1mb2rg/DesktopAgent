using DesktopAgent.Poc;

namespace DesktopAgent.Tests.Poc;

public sealed class M3TrayWindowLifecycleStateTests
{
    [Fact]
    public void FreshState_AllowsTrayOpen()
    {
        var state = new M3TrayWindowLifecycleState();

        Assert.True(state.CanOpenFromTray);
        Assert.False(state.IsExitRequested);
        Assert.False(state.IsMainWindowClosed);
        Assert.False(state.CanShowMainWindowForExit);
    }

    [Fact]
    public void BeginExit_BlocksConcurrentTrayOpen_ButAllowsExitToRevealLiveMainWindow()
    {
        var state = new M3TrayWindowLifecycleState();

        Assert.True(state.TryBeginExit());

        Assert.True(state.IsExitRequested);
        Assert.False(state.CanOpenFromTray);
        Assert.True(state.CanShowMainWindowForExit);
        Assert.False(state.TryBeginExit());
    }

    [Fact]
    public void CancelExit_BeforeWindowClosed_RestoresTrayOpenEligibility()
    {
        var state = new M3TrayWindowLifecycleState();
        Assert.True(state.TryBeginExit());

        state.CancelExit();

        Assert.False(state.IsExitRequested);
        Assert.False(state.IsMainWindowClosed);
        Assert.True(state.CanOpenFromTray);
    }

    [Fact]
    public void MainWindowClosed_IsTerminalAndCannotBeShownAgain()
    {
        var state = new M3TrayWindowLifecycleState();

        state.MarkMainWindowClosed();

        Assert.True(state.IsMainWindowClosed);
        Assert.False(state.CanOpenFromTray);
        Assert.False(state.CanShowMainWindowForExit);
        Assert.False(state.TryBeginExit());
    }

    [Fact]
    public void CancelExit_AfterWindowClosed_DoesNotResurrectTrayOpenEligibility()
    {
        var state = new M3TrayWindowLifecycleState();
        Assert.True(state.TryBeginExit());
        state.MarkMainWindowClosed();

        state.CancelExit();

        Assert.True(state.IsExitRequested);
        Assert.True(state.IsMainWindowClosed);
        Assert.False(state.CanOpenFromTray);
        Assert.False(state.CanShowMainWindowForExit);
    }
}
