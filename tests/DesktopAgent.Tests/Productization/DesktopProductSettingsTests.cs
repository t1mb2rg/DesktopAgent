using DesktopAgent.Core.Automation;
using DesktopAgent.Core.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopProductSettingsTests
{
    [Fact]
    public void Default_IsAssisted_FirstRunIncomplete_AiDisabled_AndStandardOverlay()
    {
        var settings = DesktopProductSettings.CreateDefault();

        Assert.Equal(DesktopAutomationPolicy.DefaultMode, settings.AutomationMode);
        Assert.Equal(DesktopAutomationMode.Assisted, settings.AutomationMode);
        Assert.False(settings.FirstRunCompleted);
        Assert.False(settings.AiEnabled);
        Assert.Null(settings.AiBaseUri);
        Assert.Null(settings.AiModel);
        Assert.Null(settings.AiSecretReference);
        Assert.Equal(DesktopOverlayAppearancePreset.Standard, settings.OverlayAppearance.Preset);
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

    [Fact]
    public void CrossPageUpdates_PreserveOverlayAndActiveLayout()
    {
        var layoutId = Guid.NewGuid();
        var appearance = new DesktopOverlayAppearanceSettings(
            DesktopOverlayAppearancePreset.Custom,
            4.5d,
            0.66d,
            0.11d,
            20d);
        var original = DesktopProductSettings.CreateDefault()
            .WithActiveLayoutTemplate(layoutId)
            .WithOverlayAppearance(appearance);

        var switchedLayout = original.WithActiveLayoutTemplate(Guid.NewGuid());
        var changedAppearance = switchedLayout.WithOverlayAppearance(
            DesktopOverlayAppearanceSettings.FromPreset(DesktopOverlayAppearancePreset.Light));

        Assert.Same(appearance, switchedLayout.OverlayAppearance);
        Assert.NotNull(changedAppearance.ActiveLayoutTemplateId);
        Assert.Equal(DesktopOverlayAppearancePreset.Light, changedAppearance.OverlayAppearance.Preset);
    }
}
