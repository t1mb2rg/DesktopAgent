using DesktopAgent.Core.Automation;
using DesktopAgent.Core.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopProductSettingsTests
{
    [Fact]
    public void Default_IsAssisted_FirstRunIncomplete_AndAiDisabled()
    {
        var settings = DesktopProductSettings.CreateDefault();

        Assert.Equal(DesktopAutomationPolicy.DefaultMode, settings.AutomationMode);
        Assert.Equal(DesktopAutomationMode.Assisted, settings.AutomationMode);
        Assert.False(settings.FirstRunCompleted);
        Assert.False(settings.AiEnabled);
        Assert.Null(settings.AiBaseUri);
        Assert.Null(settings.AiModel);
        Assert.Null(settings.AiSecretReference);
    }

    [Fact]
    public void AiConfiguration_RequiresCompatibleProviderShape_ButAllowsLocalNoAuth()
    {
        var local = new DesktopProductSettings(
            DesktopAutomationMode.Automatic,
            firstRunCompleted: true,
            aiEnabled: true,
            new Uri("http://127.0.0.1:8000/v1"),
            "Qwen2.5-7B-Instruct",
            aiSecretReference: null);

        Assert.True(local.AiEnabled);
        Assert.Null(local.AiSecretReference);

        Assert.Throws<ArgumentException>(() => new DesktopProductSettings(
            DesktopAutomationMode.Assisted,
            false,
            true,
            aiBaseUri: null,
            aiModel: null,
            aiSecretReference: null));

        Assert.Throws<ArgumentException>(() => new DesktopProductSettings(
            DesktopAutomationMode.Assisted,
            false,
            false,
            new Uri("https://user:password@example.com/v1"),
            "model",
            null));
    }
}
