using System.Text.Json;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Persistence.DesktopLayouts.Templates;

/// <summary>
/// Persists recoverable multi-display editor drafts only.
/// Preview, Candidate, execution plans, and Writer authority are deliberately excluded.
/// </summary>
public sealed class JsonDesktopLayoutWorkspaceRecoveryStore
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDesktopLayoutWorkspaceRecoveryStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Workspace recovery path 不能为空。", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public async Task<DesktopLayoutWorkspaceRecoveryState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return null;

            PersistedWorkspace? persisted;
            try
            {
                var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
                persisted = JsonSerializer.Deserialize<PersistedWorkspace>(json, SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("多显示器工作区恢复文件不是有效 JSON。", ex);
            }

            if (persisted is null)
                throw new InvalidDataException("多显示器工作区恢复文件为空。");
            if (persisted.FormatVersion != CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"不支持工作区恢复格式 {persisted.FormatVersion}，当前仅支持 {CurrentFormatVersion}。");
            }

            try
            {
                var displays = persisted.Displays.Select(RestoreDisplay).ToArray();
                var migrations = persisted.Migrations.Select(RestoreMigration).ToArray();
                return new DesktopLayoutWorkspaceRecoveryState(
                    persisted.ActiveDeviceName,
                    displays,
                    migrations);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
            {
                throw new InvalidDataException("多显示器工作区恢复内容不符合当前领域约束。", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        DesktopLayoutWorkspaceRecoveryState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("无法解析工作区恢复目录。");
        Directory.CreateDirectory(directory);
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            var persisted = new PersistedWorkspace
            {
                FormatVersion = CurrentFormatVersion,
                ActiveDeviceName = state.ActiveDeviceName,
                Displays = state.Displays.Select(PersistDisplay).ToList(),
                Migrations = state.Migrations.Select(PersistMigration).ToList(),
            };
            var json = JsonSerializer.Serialize(persisted, SerializerOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DesktopLayoutWorkspaceDraftRecoveryState RestoreDisplay(PersistedDisplay item)
    {
        var display = new DisplayDescriptor(
            item.DeviceName,
            new PixelRect(item.BoundsLeft, item.BoundsTop, item.BoundsRight, item.BoundsBottom),
            new PixelRect(item.WorkLeft, item.WorkTop, item.WorkRight, item.WorkBottom),
            item.DpiX,
            item.DpiY,
            item.IsPrimary);
        var zones = item.Zones.Select(zone => new DesktopLayoutZone(
            zone.ZoneId,
            zone.Name,
            new NormalizedRect(zone.Left, zone.Top, zone.Right, zone.Bottom),
            zone.SortOrder,
            (ZonePlacementFlow)zone.PlacementFlow,
            zone.MaxItemCount)).ToArray();
        var rules = item.Rules.Select(RestoreRule).ToArray();

        return new DesktopLayoutWorkspaceDraftRecoveryState(
            display,
            item.DisplayRef,
            item.ObservedIconCount,
            item.SourceAssignmentCount,
            item.MappedAssignmentCount,
            item.TemplateId,
            item.SourceVersion,
            item.Name,
            zones,
            new DesktopLayoutIconRuleSet(item.TemplateId, rules),
            item.ExplicitInboundStableIds);
    }

    private static DesktopLayoutWorkspaceMigrationRecoveryState RestoreMigration(PersistedMigration item)
        => new(
            item.StableId,
            item.SourceDeviceName,
            item.TargetDeviceName,
            item.TargetZoneId,
            RestoreRule(item.SourceRuleBeforeMigration));

    private static DesktopLayoutIconRule RestoreRule(PersistedRule rule)
        => new(rule.StableId, (DesktopLayoutIconRuleKind)rule.Kind, rule.TargetZoneId);

    private static PersistedDisplay PersistDisplay(DesktopLayoutWorkspaceDraftRecoveryState item)
        => new()
        {
            DeviceName = item.Display.DeviceName,
            BoundsLeft = item.Display.Bounds.Left,
            BoundsTop = item.Display.Bounds.Top,
            BoundsRight = item.Display.Bounds.Right,
            BoundsBottom = item.Display.Bounds.Bottom,
            WorkLeft = item.Display.WorkArea.Left,
            WorkTop = item.Display.WorkArea.Top,
            WorkRight = item.Display.WorkArea.Right,
            WorkBottom = item.Display.WorkArea.Bottom,
            DpiX = item.Display.DpiX,
            DpiY = item.Display.DpiY,
            IsPrimary = item.Display.IsPrimary,
            DisplayRef = item.DisplayRef,
            ObservedIconCount = item.ObservedIconCount,
            SourceAssignmentCount = item.SourceAssignmentCount,
            MappedAssignmentCount = item.MappedAssignmentCount,
            TemplateId = item.TemplateId,
            SourceVersion = item.SourceVersion,
            Name = item.Name,
            Zones = item.Zones.Select(zone => new PersistedZone
            {
                ZoneId = zone.ZoneId,
                Name = zone.Name,
                Left = zone.Bounds.Left,
                Top = zone.Bounds.Top,
                Right = zone.Bounds.Right,
                Bottom = zone.Bounds.Bottom,
                SortOrder = zone.SortOrder,
                PlacementFlow = (int)zone.PlacementFlow,
                MaxItemCount = zone.MaxItemCount,
            }).ToList(),
            Rules = item.RuleSet.Rules.Select(PersistRule).ToList(),
            ExplicitInboundStableIds = item.ExplicitInboundStableIds.ToList(),
        };

    private static PersistedMigration PersistMigration(DesktopLayoutWorkspaceMigrationRecoveryState item)
        => new()
        {
            StableId = item.StableId,
            SourceDeviceName = item.SourceDeviceName,
            TargetDeviceName = item.TargetDeviceName,
            TargetZoneId = item.TargetZoneId,
            SourceRuleBeforeMigration = PersistRule(item.SourceRuleBeforeMigration),
        };

    private static PersistedRule PersistRule(DesktopLayoutIconRule rule)
        => new()
        {
            StableId = rule.StableId,
            Kind = (int)rule.Kind,
            TargetZoneId = rule.TargetZoneId,
        };

    private sealed class PersistedWorkspace
    {
        public int FormatVersion { get; set; }
        public string ActiveDeviceName { get; set; } = string.Empty;
        public List<PersistedDisplay> Displays { get; set; } = [];
        public List<PersistedMigration> Migrations { get; set; } = [];
    }

    private sealed class PersistedDisplay
    {
        public string DeviceName { get; set; } = string.Empty;
        public int BoundsLeft { get; set; }
        public int BoundsTop { get; set; }
        public int BoundsRight { get; set; }
        public int BoundsBottom { get; set; }
        public int WorkLeft { get; set; }
        public int WorkTop { get; set; }
        public int WorkRight { get; set; }
        public int WorkBottom { get; set; }
        public uint DpiX { get; set; }
        public uint DpiY { get; set; }
        public bool IsPrimary { get; set; }
        public string DisplayRef { get; set; } = string.Empty;
        public int ObservedIconCount { get; set; }
        public int SourceAssignmentCount { get; set; }
        public int MappedAssignmentCount { get; set; }
        public Guid TemplateId { get; set; }
        public int SourceVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<PersistedZone> Zones { get; set; } = [];
        public List<PersistedRule> Rules { get; set; } = [];
        public List<string> ExplicitInboundStableIds { get; set; } = [];
    }

    private sealed class PersistedZone
    {
        public Guid ZoneId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int SortOrder { get; set; }
        public int PlacementFlow { get; set; }
        public int? MaxItemCount { get; set; }
    }

    private sealed class PersistedRule
    {
        public string StableId { get; set; } = string.Empty;
        public int Kind { get; set; }
        public Guid? TargetZoneId { get; set; }
    }

    private sealed class PersistedMigration
    {
        public string StableId { get; set; } = string.Empty;
        public string SourceDeviceName { get; set; } = string.Empty;
        public string TargetDeviceName { get; set; } = string.Empty;
        public Guid TargetZoneId { get; set; }
        public PersistedRule SourceRuleBeforeMigration { get; set; } = new();
    }
}
