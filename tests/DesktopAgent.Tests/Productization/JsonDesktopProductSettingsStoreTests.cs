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
        Assert.Equal(DesktopOverlayAppearancePreset.Standard, settings.OverlayAppearance.Preset);
        Assert.Equal(3d, settings.OverlayAppearance.BorderThicknessDip);
        Assert.Equal(DesktopOverlayAppearanceSettings.DefaultZoneColorRgb, settings.OverlayAppearance.ZoneColorRgb);
    }

    [Fact]
    public async Task SaveAndReload_RoundTripsOnlyNonSecretConfiguration()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var store = new JsonDesktopProductSettingsStore(path);
        var activeLayoutTemplateId = Guid.NewGuid();
        var expectedAppearance = new DesktopOverlayAppearanceSettings(
            DesktopOverlayAppearancePreset.Custom,
            borderThicknessDip: 3.5d,
            borderOpacity: 0.61d,
            fillOpacity: 0.12d,
            cornerRadiusDip: 21d,
            zoneColorRgb: 0x8A4FFFu);
        var expected = new DesktopProductSettings(
            DesktopAutomationMode.Automatic,
            firstRunCompleted: true,
            aiEnabled: true,
            new Uri("https://api.example.com/v1"),
            "example-model",
            "openai-compatible-api-key",
            activeLayoutTemplateId,
            expectedAppearance);

        await store.SaveAsync(expected);
        var actual = await new JsonDesktopProductSettingsStore(path).GetAsync();

        Assert.Equal(expected.AutomationMode, actual.AutomationMode);
        Assert.Equal(expected.FirstRunCompleted, actual.FirstRunCompleted);
        Assert.Equal(expected.AiEnabled, actual.AiEnabled);
        Assert.Equal(expected.AiBaseUri, actual.AiBaseUri);
        Assert.Equal(expected.AiModel, actual.AiModel);
        Assert.Equal(expected.AiSecretReference, actual.AiSecretReference);
        Assert.Equal(activeLayoutTemplateId, actual.ActiveLayoutTemplateId);
        Assert.Equal(expectedAppearance.Preset, actual.OverlayAppearance.Preset);
        Assert.Equal(expectedAppearance.BorderThicknessDip, actual.OverlayAppearance.BorderThicknessDip);
        Assert.Equal(expectedAppearance.BorderOpacity, actual.OverlayAppearance.BorderOpacity);
        Assert.Equal(expectedAppearance.FillOpacity, actual.OverlayAppearance.FillOpacity);
        Assert.Equal(expectedAppearance.CornerRadiusDip, actual.OverlayAppearance.CornerRadiusDip);
        Assert.Equal(expectedAppearance.ZoneColorRgb, actual.OverlayAppearance.ZoneColorRgb);

        var raw = await File.ReadAllTextAsync(path);
        Assert.Contains("openai-compatible-api-key", raw, StringComparison.Ordinal);
        Assert.Contains(activeLayoutTemplateId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overlayAppearance", raw, StringComparison.Ordinal);
        Assert.Contains("zoneColorRgb", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-secret", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyFormatVersion1WithoutActiveLayoutOrOverlayAppearance_RemainsReadable()
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
        Assert.Equal(DesktopOverlayAppearancePreset.Standard, actual.OverlayAppearance.Preset);
        Assert.Equal(0.075d, actual.OverlayAppearance.FillOpacity);
        Assert.Equal(DesktopOverlayAppearanceSettings.DefaultZoneColorRgb, actual.OverlayAppearance.ZoneColorRgb);
    }

    [Fact]
    public async Task LegacyOverlayAppearanceWithoutColor_RemainsReadableWithDefaultBlue()
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
              "aiSecretReference": null,
              "overlayAppearance": {
                "preset": 3,
                "borderThicknessDip": 4.5,
                "borderOpacity": 0.63,
                "fillOpacity": 0.11,
                "cornerRadiusDip": 20
              }
            }
            """);

        var actual = await new JsonDesktopProductSettingsStore(path).GetAsync();

        Assert.Equal(DesktopOverlayAppearancePreset.Custom, actual.OverlayAppearance.Preset);
        Assert.Equal(4.5d, actual.OverlayAppearance.BorderThicknessDip);
        Assert.Equal(DesktopOverlayAppearanceSettings.DefaultZoneColorRgb, actual.OverlayAppearance.ZoneColorRgb);
    }

    [Fact]
    public async Task InvalidOverlayAppearance_FailsClosed()
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
              "aiSecretReference": null,
              "overlayAppearance": {
                "preset": 3,
                "borderThicknessDip": 99,
                "borderOpacity": 0.5,
                "fillOpacity": 0.1,
                "cornerRadiusDip": 16,
                "zoneColorRgb": 7927932
              }
            }
            """);

        var store = new JsonDesktopProductSettingsStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync());
    }

    [Fact]
    public async Task InvalidOverlayColor_FailsClosed()
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
              "aiSecretReference": null,
              "overlayAppearance": {
                "preset": 3,
                "borderThicknessDip": 3,
                "borderOpacity": 0.5,
                "fillOpacity": 0.1,
                "cornerRadiusDip": 16,
                "zoneColorRgb": 16777216
              }
            }
            """);

        var store = new JsonDesktopProductSettingsStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync());
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
