using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.Productization;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopProductIntelligenceOptionsFactoryTests
{
    [Fact]
    public void Create_AiDisabled_ReturnsNullWithoutInventingProvider()
    {
        var settings = DesktopProductSettings.CreateDefault();

        Assert.Null(DesktopProductIntelligenceOptionsFactory.Create(settings));
    }

    [Fact]
    public void Create_AiEnabled_ReusesM4OpenAiCompatibleContract()
    {
        var settings = new DesktopProductSettings(
            DesktopAutomationMode.Assisted,
            firstRunCompleted: true,
            aiEnabled: true,
            new Uri("https://api.example.com/v1"),
            "example-model",
            "openai-compatible-api-key");

        var options = Assert.IsType<OpenAiCompatibleProviderOptions>(
            DesktopProductIntelligenceOptionsFactory.Create(settings));

        Assert.Equal(settings.AiBaseUri, options.BaseUri);
        Assert.Equal(settings.AiModel, options.Model);
        Assert.Equal(settings.AiSecretReference, options.SecretReference);
    }
}
