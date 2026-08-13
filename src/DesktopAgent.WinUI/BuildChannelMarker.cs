using DesktopAgent.Core.Productization;

namespace DesktopAgent.WinUI;

internal static class BuildChannelMarker
{
    internal static string DisplayName => TesselProductIdentity.DisplayName;
    internal static string InstanceKey => TesselProductIdentity.InstanceKey;
    internal static string DataRoot => TesselProductDataPaths.RootDirectory;
}
