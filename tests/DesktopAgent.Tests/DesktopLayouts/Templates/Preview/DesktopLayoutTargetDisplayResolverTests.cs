using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Preview;

public sealed class DesktopLayoutTargetDisplayResolverTests
{
    private static DisplayDescriptor D(string name, bool primary = false) =>
        new(name, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, primary);

    private static readonly IReadOnlyList<DisplayDescriptor> TwoDisplays =
        new[] { D("DISPLAY1", primary: true), D("DISPLAY2") };

    [Fact] public void SelectedTarget_ExactMatch() { var r = DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForSelectedTarget(), TwoDisplays, "DISPLAY2"); Assert.Equal("DISPLAY2", r.DeviceName); }
    [Fact] public void SelectedTarget_CaseSensitive() { Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForSelectedTarget(), TwoDisplays, "display2")); }
    [Fact] public void SelectedTarget_NotTrimmed() { Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForSelectedTarget(), TwoDisplays, " DISPLAY2 ")); }
    [Fact] public void SelectedTarget_Null_Fails() { Assert.Throws<ArgumentException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForSelectedTarget(), TwoDisplays, null!)); }
    [Fact] public void SelectedTarget_Whitespace_Fails() { Assert.Throws<ArgumentException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForSelectedTarget(), TwoDisplays, "  ")); }
    [Fact] public void SelectedTarget_NotFound_Fails() { Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForSelectedTarget(), TwoDisplays, "MISSING")); }
    [Fact] public void PrimaryDisplay_Unique_Success() { var r = DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), TwoDisplays, null); Assert.Equal("DISPLAY1", r.DeviceName); }
    [Fact] public void PrimaryDisplay_None_Fails() { var no = new[] { D("A"), D("B") }; Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), no, null)); }
    [Fact] public void PrimaryDisplay_Multiple_Fails() { var multi = new[] { D("A", true), D("B", true) }; Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), multi, null)); }
    [Fact] public void PrimaryDisplay_IgnoresSelectedTarget() { var r = DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), TwoDisplays, "DISPLAY2"); Assert.Equal("DISPLAY1", r.DeviceName); }
    [Fact] public void ExactDeviceName_Match() { var r = DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForExactDeviceName("DISPLAY2"), TwoDisplays, null); Assert.Equal("DISPLAY2", r.DeviceName); }
    [Fact] public void ExactDeviceName_CaseSensitive() { Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForExactDeviceName("display2"), TwoDisplays, null)); }
    [Fact] public void ExactDeviceName_NotTrimmed() { Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForExactDeviceName(" DISPLAY2 "), TwoDisplays, null)); }
    [Fact] public void ExactDeviceName_NotFound_Fails() { Assert.Throws<InvalidOperationException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForExactDeviceName("MISSING"), TwoDisplays, null)); }
    [Fact] public void DuplicateDeviceName_Fails() { var dup = new[] { D("A"), D("A") }; Assert.Throws<ArgumentException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), dup, null)); }
    [Fact] public void NullDisplays_Fails() { Assert.Throws<ArgumentNullException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), null!, null)); }
    [Fact] public void NullDisplayElement_Fails() { var bad = new DisplayDescriptor[] { null! }; Assert.Throws<ArgumentException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), bad, null)); }
    [Fact] public void EmptyDeviceName_Fails() { var bad = new[] { new DisplayDescriptor("", new PixelRect(0, 0, 10, 10), new PixelRect(0, 0, 10, 10), 96, 96, true) }; Assert.Throws<ArgumentException>(() => DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), bad, null)); }
    [Fact] public void DoesNotModifyInputCollection() { var list = new List<DisplayDescriptor>(TwoDisplays); var count = list.Count; DesktopLayoutTargetDisplayResolver.Resolve(TargetDisplayBinding.ForPrimaryDisplay(), list, null); Assert.Equal(count, list.Count); }
}
