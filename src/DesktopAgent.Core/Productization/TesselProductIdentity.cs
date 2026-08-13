namespace DesktopAgent.Core.Productization;

public enum TesselProductChannel
{
    Dev = 0,
    PackagingTest = 1,
    Stable = 2,
}

/// <summary>
/// Tessel 运行身份。构建渠道只决定产品显示名、单实例隔离与本地数据根目录；
/// 不改变 Automation、Writer 或 Recovery 语义。
/// </summary>
public static class TesselProductIdentity
{
#if TESSEL_DEV_CHANNEL
    public const TesselProductChannel Channel = TesselProductChannel.Dev;
    public const string DisplayName = "Tessel Dev";
    public const string InstanceKey = "Tessel.ProductShell.Dev";
    public const string LocalDataDirectoryName = "Tessel.Dev";
#elif TESSEL_PACKAGING_TEST_CHANNEL
    public const TesselProductChannel Channel = TesselProductChannel.PackagingTest;
    public const string DisplayName = "Tessel Packaging Test";
    public const string InstanceKey = "Tessel.ProductShell.PackagingTest";
    public const string LocalDataDirectoryName = "Tessel.PackagingTest";
#else
    public const TesselProductChannel Channel = TesselProductChannel.Stable;
    public const string DisplayName = "Tessel";
    public const string InstanceKey = "Tessel.ProductShell.Stable";
    public const string LocalDataDirectoryName = "Tessel";
#endif

    public static bool IsDevelopment => Channel == TesselProductChannel.Dev;
    public static bool IsPackagingTest => Channel == TesselProductChannel.PackagingTest;
    public static bool IsStable => Channel == TesselProductChannel.Stable;
}
