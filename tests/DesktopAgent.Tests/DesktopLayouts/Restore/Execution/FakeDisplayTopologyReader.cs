using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class FakeDisplayTopologyReader : IDisplayTopologyReader
{
    private readonly IReadOnlyList<DisplayDescriptor> _displays;

    public FakeDisplayTopologyReader(IReadOnlyList<DisplayDescriptor> displays)
    {
        _displays = displays;
    }

    public FakeDisplayTopologyReader(string deviceName, PixelRect workArea)
        : this(new[] { new DisplayDescriptor(deviceName, workArea, workArea, 96, 96, true) })
    { }

    public IReadOnlyList<DisplayDescriptor> GetDisplays() => _displays;
}
