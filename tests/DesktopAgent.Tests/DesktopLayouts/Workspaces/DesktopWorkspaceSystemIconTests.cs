using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopWorkspaceSystemIconTests
{
    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    [Theory]
    [InlineData(RecycleBin, RecycleBin, true)]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true)]
    [InlineData("display:回收站", null, false)]
    [InlineData("display:回收站", RecycleBin, false)]
    [InlineData("::{not-a-guid}", "::{not-a-guid}", false)]
    [InlineData("", "", false)]
    public void SystemIconRequiresReliableShellIdentity(string stableId, string? parsingName, bool expected)
    {
        var icon = new DesktopIconDescriptor(stableId, "回收站", parsingName, ItemKind.SystemItem,
            new PixelPoint(30, 50), "DISPLAY1", true, false);
        Assert.Equal(expected, DesktopWorkspaceLocalItemFactProjector.IsMovable(icon));
    }
}
