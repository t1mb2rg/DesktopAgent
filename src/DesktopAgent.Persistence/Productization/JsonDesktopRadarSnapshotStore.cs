using System.Text.Json;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Productization;

namespace DesktopAgent.Persistence.Productization;

/// <summary>
/// Desktop Radar 的 presentation-only JSON cache。
/// Must not be used as execution evidence.
/// 该 Store 不参与 Layout / Planner / Writer / Recovery 持久化语义。
/// </summary>
public sealed class JsonDesktopRadarSnapshotStore
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDesktopRadarSnapshotStore(string cachePath)
    {
        if (string.IsNullOrWhiteSpace(cachePath))
            throw new ArgumentException("Radar cache path 不能为空。", nameof(cachePath));

        _cachePath = Path.GetFullPath(cachePath);
    }

    public async Task<DesktopRadarSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = await File.ReadAllTextAsync(_cachePath, cancellationToken);
            PersistedRadarSnapshot? persisted;
            try
            {
                persisted = JsonSerializer.Deserialize<PersistedRadarSnapshot>(json, SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Desktop Radar cache 不是有效 JSON。", ex);
            }

            if (persisted is null)
                throw new InvalidDataException("Desktop Radar cache 为空。");
            if (persisted.FormatVersion != CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"Desktop Radar cache formatVersion={persisted.FormatVersion}，当前仅支持 {CurrentFormatVersion}。");
            }

            try
            {
                var displays = persisted.Displays.Select(display =>
                    new DesktopRadarDisplaySnapshot(
                        display.DisplayKey,
                        new PixelRect(display.Left, display.Top, display.Right, display.Bottom),
                        display.IsPrimary,
                        display.ItemCount,
                        display.GridColumns,
                        display.GridRows,
                        display.DensityCells.Select(cell =>
                            new DesktopRadarDensityCell(
                                cell.Column,
                                cell.Row,
                                (DesktopRadarDensityLevel)cell.Density)).ToArray()))
                    .ToArray();

                return new DesktopRadarSnapshot(
                    persisted.ObservationId,
                    persisted.ObservedAtUtc,
                    persisted.DisplayTopologyFingerprint,
                    persisted.LayoutTemplateId,
                    persisted.LayoutVersion,
                    displays);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException("Desktop Radar cache 内容不符合当前 presentation contract。", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        DesktopRadarSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken);

        var directory = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException("无法解析 Desktop Radar cache 目录。");
        Directory.CreateDirectory(directory);
        var tempPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            var persisted = new PersistedRadarSnapshot
            {
                FormatVersion = CurrentFormatVersion,
                ObservationId = snapshot.ObservationId,
                ObservedAtUtc = snapshot.ObservedAtUtc,
                DisplayTopologyFingerprint = snapshot.DisplayTopologyFingerprint,
                LayoutTemplateId = snapshot.LayoutTemplateId,
                LayoutVersion = snapshot.LayoutVersion,
                Displays = snapshot.Displays.Select(display => new PersistedDisplay
                {
                    DisplayKey = display.DisplayKey,
                    Left = display.Bounds.Left,
                    Top = display.Bounds.Top,
                    Right = display.Bounds.Right,
                    Bottom = display.Bounds.Bottom,
                    IsPrimary = display.IsPrimary,
                    ItemCount = display.ItemCount,
                    GridColumns = display.GridColumns,
                    GridRows = display.GridRows,
                    DensityCells = display.DensityCells.Select(cell => new PersistedDensityCell
                    {
                        Column = cell.Column,
                        Row = cell.Row,
                        Density = (int)cell.Density,
                    }).ToList(),
                }).ToList(),
            };

            var json = JsonSerializer.Serialize(persisted, SerializerOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, _cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            _gate.Release();
        }
    }

    private sealed class PersistedRadarSnapshot
    {
        public int FormatVersion { get; set; }
        public Guid ObservationId { get; set; }
        public DateTimeOffset ObservedAtUtc { get; set; }
        public string DisplayTopologyFingerprint { get; set; } = string.Empty;
        public Guid? LayoutTemplateId { get; set; }
        public int? LayoutVersion { get; set; }
        public List<PersistedDisplay> Displays { get; set; } = [];
    }

    private sealed class PersistedDisplay
    {
        public string DisplayKey { get; set; } = string.Empty;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public bool IsPrimary { get; set; }
        public int ItemCount { get; set; }
        public int GridColumns { get; set; }
        public int GridRows { get; set; }
        public List<PersistedDensityCell> DensityCells { get; set; } = [];
    }

    private sealed class PersistedDensityCell
    {
        public int Column { get; set; }
        public int Row { get; set; }
        public int Density { get; set; }
    }
}
