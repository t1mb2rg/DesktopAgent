using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class FakeDesktopIconReader : IDesktopIconReader
{
    private readonly DesktopIconReadResult _result;

    public FakeDesktopIconReader(DesktopIconReadResult result)
    {
        _result = result;
    }

    public FakeDesktopIconReader(IReadOnlyList<DesktopIconDescriptor> icons)
        : this(new DesktopIconReadResult(icons, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null))
    { }

    public DesktopIconReadResult ReadDesktopIcons() => _result;
}
