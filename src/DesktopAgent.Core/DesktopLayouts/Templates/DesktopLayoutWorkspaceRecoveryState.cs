using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// A recoverable editing snapshot for one display in a multi-display workspace.
/// This is draft state only: it never contains Preview, Candidate, Writer, or recovery authority.
/// </summary>
public sealed record DesktopLayoutWorkspaceDraftRecoveryState
{
    public DisplayDescriptor Display { get; }
    public string DisplayRef { get; }
    public int ObservedIconCount { get; }
    public int SourceAssignmentCount { get; }
    public int MappedAssignmentCount { get; }
    public Guid TemplateId { get; }
    public int SourceVersion { get; }
    public string Name { get; }
    public IReadOnlyList<DesktopLayoutZone> Zones { get; }
    public DesktopLayoutIconRuleSet RuleSet { get; }
    public IReadOnlyList<string> ExplicitInboundStableIds { get; }

    public DesktopLayoutWorkspaceDraftRecoveryState(
        DisplayDescriptor display,
        string displayRef,
        int observedIconCount,
        int sourceAssignmentCount,
        int mappedAssignmentCount,
        Guid templateId,
        int sourceVersion,
        string name,
        IReadOnlyList<DesktopLayoutZone> zones,
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlyCollection<string>? explicitInboundStableIds = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));
        if (observedIconCount < 0)
            throw new ArgumentOutOfRangeException(nameof(observedIconCount));
        if (sourceAssignmentCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceAssignmentCount));
        if (mappedAssignmentCount < 0 || mappedAssignmentCount > sourceAssignmentCount)
            throw new ArgumentOutOfRangeException(nameof(mappedAssignmentCount));
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (sourceVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(ruleSet);
        if (ruleSet.TemplateId != templateId)
            throw new ArgumentException("RuleSet.TemplateId 必须与 TemplateId 一致。", nameof(ruleSet));

        _ = new DesktopLayoutTemplate(
            templateId,
            name,
            sourceVersion == 0 ? 1 : sourceVersion,
            TargetDisplayBinding.ForExactDeviceName(display.DeviceName),
            zones);

        var inbound = (explicitInboundStableIds ?? Array.Empty<string>())
            .OrderBy(static stableId => stableId, StringComparer.Ordinal)
            .ToArray();
        if (inbound.Length != inbound.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("ExplicitInboundStableIds 不能重复。", nameof(explicitInboundStableIds));

        var zoneOverrides = ruleSet.Rules
            .Where(static rule => rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
            .Select(static rule => rule.StableId)
            .ToHashSet(StringComparer.Ordinal);
        if (inbound.Any(stableId => string.IsNullOrWhiteSpace(stableId) || !zoneOverrides.Contains(stableId)))
        {
            throw new ArgumentException(
                "每个 explicit inbound StableId 都必须拥有 ZoneOverride 规则。",
                nameof(explicitInboundStableIds));
        }

        Display = display;
        DisplayRef = displayRef;
        ObservedIconCount = observedIconCount;
        SourceAssignmentCount = sourceAssignmentCount;
        MappedAssignmentCount = mappedAssignmentCount;
        TemplateId = templateId;
        SourceVersion = sourceVersion;
        Name = name.Trim();
        Zones = Array.AsReadOnly(zones.ToArray());
        RuleSet = ruleSet;
        ExplicitInboundStableIds = Array.AsReadOnly(inbound);
    }
}

public sealed record DesktopLayoutWorkspaceMigrationRecoveryState
{
    public string StableId { get; }
    public string SourceDeviceName { get; }
    public string TargetDeviceName { get; }
    public Guid TargetZoneId { get; }
    public DesktopLayoutIconRule SourceRuleBeforeMigration { get; }

    public DesktopLayoutWorkspaceMigrationRecoveryState(
        string stableId,
        string sourceDeviceName,
        string targetDeviceName,
        Guid targetZoneId,
        DesktopLayoutIconRule sourceRuleBeforeMigration)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(sourceDeviceName))
            throw new ArgumentException("SourceDeviceName 不能为空。", nameof(sourceDeviceName));
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            throw new ArgumentException("TargetDeviceName 不能为空。", nameof(targetDeviceName));
        if (string.Equals(sourceDeviceName, targetDeviceName, StringComparison.Ordinal))
            throw new ArgumentException("SourceDeviceName 与 TargetDeviceName 不能相同。", nameof(targetDeviceName));
        if (targetZoneId == Guid.Empty)
            throw new ArgumentException("TargetZoneId 不能为 Guid.Empty。", nameof(targetZoneId));
        ArgumentNullException.ThrowIfNull(sourceRuleBeforeMigration);
        if (!string.Equals(sourceRuleBeforeMigration.StableId, stableId, StringComparison.Ordinal))
            throw new ArgumentException("SourceRuleBeforeMigration.StableId 不一致。", nameof(sourceRuleBeforeMigration));

        StableId = stableId;
        SourceDeviceName = sourceDeviceName;
        TargetDeviceName = targetDeviceName;
        TargetZoneId = targetZoneId;
        SourceRuleBeforeMigration = sourceRuleBeforeMigration;
    }
}

public sealed record DesktopLayoutWorkspaceRecoveryState
{
    public string ActiveDeviceName { get; }
    public IReadOnlyList<DesktopLayoutWorkspaceDraftRecoveryState> Displays { get; }
    public IReadOnlyList<DesktopLayoutWorkspaceMigrationRecoveryState> Migrations { get; }

    public DesktopLayoutWorkspaceRecoveryState(
        string activeDeviceName,
        IReadOnlyList<DesktopLayoutWorkspaceDraftRecoveryState> displays,
        IReadOnlyList<DesktopLayoutWorkspaceMigrationRecoveryState>? migrations = null)
    {
        if (string.IsNullOrWhiteSpace(activeDeviceName))
            throw new ArgumentException("ActiveDeviceName 不能为空。", nameof(activeDeviceName));
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count < 2)
            throw new ArgumentException("多显示器工作区至少需要两块显示器。", nameof(displays));

        var devices = displays.Select(static item => item.Display.DeviceName).ToArray();
        if (devices.Distinct(StringComparer.Ordinal).Count() != devices.Length)
            throw new ArgumentException("工作区显示器 DeviceName 不能重复。", nameof(displays));
        if (!devices.Contains(activeDeviceName, StringComparer.Ordinal))
            throw new ArgumentException("ActiveDeviceName 不在工作区中。", nameof(activeDeviceName));

        var migrationArray = (migrations ?? Array.Empty<DesktopLayoutWorkspaceMigrationRecoveryState>())
            .OrderBy(static item => item.StableId, StringComparer.Ordinal)
            .ToArray();
        if (migrationArray.Select(static item => item.StableId).Distinct(StringComparer.Ordinal).Count() != migrationArray.Length)
            throw new ArgumentException("Migration StableId 不能重复。", nameof(migrations));
        if (migrationArray.Any(item =>
                !devices.Contains(item.SourceDeviceName, StringComparer.Ordinal) ||
                !devices.Contains(item.TargetDeviceName, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Migration 引用了工作区外的显示器。", nameof(migrations));
        }

        ActiveDeviceName = activeDeviceName;
        Displays = Array.AsReadOnly(displays.ToArray());
        Migrations = Array.AsReadOnly(migrationArray);
    }
}
