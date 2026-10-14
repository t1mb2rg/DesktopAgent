using DesktopAgent.Persistence.DesktopLayouts;

namespace DesktopAgent.Tests.Persistence;

/// <summary>
/// 数据根目录可配（环境变量 <see cref="DesktopAgentDataPaths.RootDirectoryEnvironmentVariable"/>）：
/// 验收演练靠它把引擎指到一份带真实站定授权的库上，而不碰进程平时用的
/// %LocalAppData%\DesktopAgent。产品壳走按渠道解析的 TesselProductDataPaths，不受影响。
/// </summary>
public sealed class DesktopAgentDataPathsTests
{
    [Fact]
    public void ResolveRootDirectory_UsesTheOverrideWhenItIsSet()
    {
        var overridden = Path.Combine(Path.GetTempPath(), "desktop-agent-datapath-override");

        Assert.Equal(Path.GetFullPath(overridden), DesktopAgentDataPaths.ResolveRootDirectory(overridden));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRootDirectory_FallsBackToLocalAppDataWhenBlank(string? value)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopAgent");

        Assert.Equal(expected, DesktopAgentDataPaths.ResolveRootDirectory(value));
    }

    [Fact]
    public void DerivedPaths_FollowTheEnvironmentOverride()
    {
        var variable = DesktopAgentDataPaths.RootDirectoryEnvironmentVariable;
        var saved = Environment.GetEnvironmentVariable(variable);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "desktop-agent-datapath-test"));
        try
        {
            Environment.SetEnvironmentVariable(variable, root);

            Assert.Equal(Path.Combine(root, "desktop-agent.db"), DesktopAgentDataPaths.DefaultDatabasePath);
            Assert.Equal(Path.Combine(root, "settings.json"), DesktopAgentDataPaths.DefaultSettingsPath);
            Assert.Equal(Path.Combine(root, "secrets"), DesktopAgentDataPaths.DefaultSecretsDirectory);
            Assert.Equal(
                Path.Combine(root, "cache", "desktop-radar.json"),
                DesktopAgentDataPaths.DefaultRadarCachePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }

        Assert.Equal(saved, Environment.GetEnvironmentVariable(variable));
    }

    [Fact]
    public void RootDirectoryEnvironmentVariable_HasTheDocumentedName()
    {
        Assert.Equal("DESKTOPAGENT_DATA_DIR", DesktopAgentDataPaths.RootDirectoryEnvironmentVariable);
    }
}
