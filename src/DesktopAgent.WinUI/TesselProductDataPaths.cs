using DesktopAgent.Core.Productization;

namespace DesktopAgent.WinUI;

internal static class TesselProductDataPaths
{
    internal static string RootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            TesselProductIdentity.LocalDataDirectoryName);

    internal static string DatabasePath =>
        Path.Combine(RootDirectory, "desktop-agent.db");

    internal static string SettingsPath =>
        Path.Combine(RootDirectory, "settings.json");

    internal static string SecretStoreDirectory =>
        Path.Combine(RootDirectory, "secrets");

    internal static string RadarCachePath =>
        Path.Combine(RootDirectory, "cache", "desktop-radar.json");

    internal static string AiWorkspaceRecoveryPath =>
        Path.Combine(RootDirectory, "recovery", "ai-multi-display-workspace.json");
}
