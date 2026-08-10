using DesktopAgent.Core.Automation;
using DesktopAgent.Core.Productization;
using DesktopAgent.Persistence.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class JsonDesktopProductSettingsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"DesktopAgent.Tests.ProductSettings.{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task MissingFile_ReturnsSafeDefault()
    {
        var store = new JsonDesktopProductSettingsStore(Path.Combine(_tempDir, "settings.json"));

        var settings = await store.GetAsync();

        Assert.Equal(DesktopAutomationMode.Assisted, settings.AutomationMode);
        Assert.False(settings.FirstRunCompleted);
        Assert.False(settings.AiEnabled);
        Assert.Null(settings.ActiveLayoutTemplateId);
    }

    [Fact]
    public async Task SaveAndReload_RoundTripsOnlyNonSecretConfiguration()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var store = new JsonDesktopProductSettingsStore(path);
        var activeLayoutTemplateId = Guid.NewGuid();
        var expected = new DesktopProductSettings(
            DesktopAutomationMode.Automatic,
            firstRunCompleted: true,
            aiEnabled: true,
            new Uri("https://api.example.com/v1"),
            "example-model",
            "openai-compatible-api-key",
            activeLayoutTemplateId);

        await store.SaveAsync(expected);
        var actual = await new JsonDesktopProductSettingsStore(path).GetAsync();

        Assert.Equal(expected.AutomationMode, actual.AutomationMode);
        Assert.Equal(expected.FirstRunCompleted, actual.FirstRunCompleted);
        Assert.Equal(expected.AiEnabled, actual.AiEnabled);
        Assert.Equal(expected.AiBaseUri, actual.AiBaseUri);
        Assert.Equal(expected.AiModel, actual.AiModel);
        Assert.Equal(expected.AiSecretReference, actual.AiSecretReference);
        Assert.Equal(activeLayoutTemplateId, actual.ActiveLayoutTemplateId);

        var raw = await File.ReadAllTextAsync(path);
        Assert.Contains("openai-compatible-api-key", raw, StringComparison.Ordinal);
        Assert.Contains(activeLayoutTemplateId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-test-secret", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyFormatVersion1WithoutActiveLayout_RemainsReadable()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "formatVersion": 1,
              "automationMode": 1,
              "firstRunCompleted": true,
              "aiEnabled": false,
              "aiBaseUri": null,
              "aiModel": null,
              "aiSecretReference": null
            }
            """);

        var actual = await new JsonDesktopProductSettingsStore(path).GetAsync();

        Assert.Equal(DesktopAutomationMode.Assisted, actual.AutomationMode);
        Assert.True(actual.FirstRunCompleted);
        Assert.Null(actual.ActiveLayoutTemplateId);
    }

    [Fact]
    public async Task CorruptFile_FailsClosed_InsteadOfSilentlyResetting()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, "{ definitely-not-json }");
        var store = new JsonDesktopProductSettingsStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync());
    }
}
