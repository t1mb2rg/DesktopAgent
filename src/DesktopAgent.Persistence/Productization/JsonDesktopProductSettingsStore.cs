using System.Text.Json;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.Productization;

namespace DesktopAgent.Persistence.Productization;

/// <summary>
/// 小型、版本化的产品偏好配置。桌面状态/历史继续由 SQLite 管理；此文件绝不保存 API Key 本体。
/// </summary>
public sealed class JsonDesktopProductSettingsStore : IDesktopProductSettingsStore
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDesktopProductSettingsStore(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
            throw new ArgumentException("SettingsPath 不能为空。", nameof(settingsPath));

        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("无法解析 Settings 目录。");
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    public async Task<DesktopProductSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath))
                return DesktopProductSettings.CreateDefault();

            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
            PersistedSettings? persisted;
            try
            {
                persisted = JsonSerializer.Deserialize<PersistedSettings>(json, SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("产品设置文件不是有效 JSON。", ex);
            }

            if (persisted is null)
                throw new InvalidDataException("产品设置文件为空。 ");
            if (persisted.FormatVersion != CurrentFormatVersion)
                throw new InvalidDataException(
                    $"产品设置 formatVersion={persisted.FormatVersion}，当前仅支持 {CurrentFormatVersion}。");

            try
            {
                var baseUri = persisted.AiBaseUri is null
                    ? null
                    : new Uri(persisted.AiBaseUri, UriKind.Absolute);

                var overlayAppearance = persisted.OverlayAppearance is null
                    ? DesktopOverlayAppearanceSettings.Standard
                    : new DesktopOverlayAppearanceSettings(
                        (DesktopOverlayAppearancePreset)persisted.OverlayAppearance.Preset,
                        persisted.OverlayAppearance.BorderThicknessDip,
                        persisted.OverlayAppearance.BorderOpacity,
                        persisted.OverlayAppearance.FillOpacity,
                        persisted.OverlayAppearance.CornerRadiusDip,
                        persisted.OverlayAppearance.ZoneColorRgb
                            ?? DesktopOverlayAppearanceSettings.DefaultZoneColorRgb);

                var overlayZoneColors = persisted.OverlayZoneColors is null
                    ? DesktopOverlayZoneColorPreferences.Empty
                    : new DesktopOverlayZoneColorPreferences(
                        persisted.OverlayZoneColors.Select(item =>
                            new KeyValuePair<Guid, uint>(item.ZoneId, item.ColorRgb)));

                return new DesktopProductSettings(
                    (DesktopAutomationMode)persisted.AutomationMode,
                    persisted.FirstRunCompleted,
                    persisted.AiEnabled,
                    baseUri,
                    persisted.AiModel,
                    persisted.AiSecretReference,
                    persisted.ActiveLayoutTemplateId,
                    overlayAppearance,
                    overlayZoneColors);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new InvalidDataException("产品设置内容不符合当前合同。", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        DesktopProductSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var persisted = new PersistedSettings
            {
                FormatVersion = CurrentFormatVersion,
                AutomationMode = (int)settings.AutomationMode,
                FirstRunCompleted = settings.FirstRunCompleted,
                AiEnabled = settings.AiEnabled,
                AiBaseUri = settings.AiBaseUri?.AbsoluteUri,
                AiModel = settings.AiModel,
                AiSecretReference = settings.AiSecretReference,
                ActiveLayoutTemplateId = settings.ActiveLayoutTemplateId,
                OverlayAppearance = new PersistedOverlayAppearance
                {
                    Preset = (int)settings.OverlayAppearance.Preset,
                    BorderThicknessDip = settings.OverlayAppearance.BorderThicknessDip,
                    BorderOpacity = settings.OverlayAppearance.BorderOpacity,
                    FillOpacity = settings.OverlayAppearance.FillOpacity,
                    CornerRadiusDip = settings.OverlayAppearance.CornerRadiusDip,
                    ZoneColorRgb = settings.OverlayAppearance.ZoneColorRgb,
                },
                OverlayZoneColors = settings.OverlayZoneColors.Overrides
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new PersistedOverlayZoneColor
                    {
                        ZoneId = pair.Key,
                        ColorRgb = pair.Value,
                    })
                    .ToList(),
            };

            var json = JsonSerializer.Serialize(persisted, SerializerOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            _gate.Release();
        }
    }

    private sealed class PersistedSettings
    {
        public int FormatVersion { get; set; }
        public int AutomationMode { get; set; }
        public bool FirstRunCompleted { get; set; }
        public bool AiEnabled { get; set; }
        public string? AiBaseUri { get; set; }
        public string? AiModel { get; set; }
        public string? AiSecretReference { get; set; }
        public Guid? ActiveLayoutTemplateId { get; set; }
        public PersistedOverlayAppearance? OverlayAppearance { get; set; }
        public List<PersistedOverlayZoneColor>? OverlayZoneColors { get; set; }
    }

    private sealed class PersistedOverlayAppearance
    {
        public int Preset { get; set; }
        public double BorderThicknessDip { get; set; }
        public double BorderOpacity { get; set; }
        public double FillOpacity { get; set; }
        public double CornerRadiusDip { get; set; }
        public uint? ZoneColorRgb { get; set; }
    }

    private sealed class PersistedOverlayZoneColor
    {
        public Guid ZoneId { get; set; }
        public uint ColorRgb { get; set; }
    }
}
