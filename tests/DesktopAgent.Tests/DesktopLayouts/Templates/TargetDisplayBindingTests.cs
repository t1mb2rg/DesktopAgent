using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Tests.DesktopLayouts.Templates;

public sealed class TargetDisplayBindingTests
{
    // ═══ SelectedTarget ═══

    [Fact]
    public void ForSelectedTarget_KindIsSelectedTarget()
    {
        var b = TargetDisplayBinding.ForSelectedTarget();
        Assert.Equal(TargetDisplayBindingKind.SelectedTarget, b.Kind);
    }

    [Fact]
    public void ForSelectedTarget_DeviceNameIsNull()
    {
        var b = TargetDisplayBinding.ForSelectedTarget();
        Assert.Null(b.DeviceName);
    }

    // ═══ PrimaryDisplay ═══

    [Fact]
    public void ForPrimaryDisplay_KindIsPrimaryDisplay()
    {
        var b = TargetDisplayBinding.ForPrimaryDisplay();
        Assert.Equal(TargetDisplayBindingKind.PrimaryDisplay, b.Kind);
    }

    [Fact]
    public void ForPrimaryDisplay_DeviceNameIsNull()
    {
        var b = TargetDisplayBinding.ForPrimaryDisplay();
        Assert.Null(b.DeviceName);
    }

    // ═══ ExactDeviceName ═══

    [Fact]
    public void ForExactDeviceName_PreservesOriginalString()
    {
        const string device = @"\\.\DISPLAY2";
        var b = TargetDisplayBinding.ForExactDeviceName(device);
        Assert.Equal(TargetDisplayBindingKind.ExactDeviceName, b.Kind);
        Assert.Equal(device, b.DeviceName);
    }

    [Fact]
    public void ForExactDeviceName_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TargetDisplayBinding.ForExactDeviceName(null!));
    }

    [Fact]
    public void ForExactDeviceName_Empty_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => TargetDisplayBinding.ForExactDeviceName(""));
        Assert.Contains("deviceName", ex.Message);
    }

    [Fact]
    public void ForExactDeviceName_Whitespace_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => TargetDisplayBinding.ForExactDeviceName("   "));
        Assert.Contains("deviceName", ex.Message);
    }

    [Fact]
    public void ForExactDeviceName_DoesNotTrim()
    {
        const string withSpaces = @" \\.\DISPLAY1 ";
        var b = TargetDisplayBinding.ForExactDeviceName(withSpaces);
        // 保留原值，不做 Trim
        Assert.Equal(withSpaces, b.DeviceName);
    }

    // ═══ 结构相等性 ═══

    [Fact]
    public void SameFactory_ProducesEqualRecords()
    {
        var a = TargetDisplayBinding.ForSelectedTarget();
        var b = TargetDisplayBinding.ForSelectedTarget();
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentDeviceNames_AreNotEqual()
    {
        var a = TargetDisplayBinding.ForExactDeviceName(@"\\.\DISPLAY1");
        var b = TargetDisplayBinding.ForExactDeviceName(@"\\.\DISPLAY2");
        Assert.NotEqual(a, b);
    }
}
