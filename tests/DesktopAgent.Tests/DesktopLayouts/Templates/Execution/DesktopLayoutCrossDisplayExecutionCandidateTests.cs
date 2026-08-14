using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Execution;

public sealed class DesktopLayoutCrossDisplayExecutionCandidateTests
{
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly DisplayDescriptor TargetDisplay = new(
        @"\\.\DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1040),
        96,
        96,
        true);
    private static readonly DisplayDescriptor SourceDisplay = new(
        @"\\.\DISPLAY2",
        new PixelRect(-1080, 0, 0, 1920),
        new PixelRect(-1080, 0, 0, 1920),
        96,
        96,
        false);

    [Fact]
    public void ExplicitInbound_BuildsCrossDisplayMoveRequiredCandidate()
    {
        var templateId = Guid.NewGuid();
        var zone = new DesktopLayoutZone(
            Guid.NewGuid(),
            "主屏目标区",
            new NormalizedRect(0, 0, 10000, 10000),
            0,
            ZonePlacementFlow.ColumnMajor,
            null);
        var template = new DesktopLayoutTemplate(
            templateId,
            "跨屏布局",
            1,
            TargetDisplayBinding.ForExactDeviceName(TargetDisplay.DeviceName),
            new[] { zone });
        var ruleSet = new DesktopLayoutIconRuleSet(
            templateId,
            new[] { DesktopLayoutIconRule.ForZoneOverride("cross", zone.ZoneId) });

        var icon = SnapshotIcon(
            0,
            "cross",
            new PixelPoint(-1000, 80),
            SourceDisplay.DeviceName);
        var preview = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            ruleSet,
            new[] { TargetDisplay, SourceDisplay },
            selectedTargetDeviceName: null,
            Spacing,
            new[] { icon },
            new HashSet<string>(StringComparer.Ordinal) { icon.StableId });

        Assert.Equal(0, preview.TargetDisplayIconCount);
        Assert.Equal(1, preview.InboundMigrationIconCount);
        Assert.Equal(1, preview.ManagedTargetIconCount);
        Assert.Equal(0, preview.OutOfScopeIconCount);
        Assert.Equal(1, preview.MoveRequiredCount);
        Assert.Equal(1, preview.InboundMoveRequiredCount);
        Assert.True(preview.HasCrossDisplayChanges);

        var previewItem = Assert.Single(preview.Items);
        Assert.Equal(DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired, previewItem.PlacementState);
        Assert.Equal(SourceDisplay.DeviceName, previewItem.Icon.MonitorDeviceName);
        Assert.True(previewItem.TargetPosition.HasValue);
        Assert.True(DesktopIconLayoutGeometry.IsFullyContained(
            DesktopIconLayoutGeometry.CreateRectChecked(previewItem.TargetPosition.Value, Spacing),
            TargetDisplay.WorkArea));

        var currentRead = Read(icon);
        var build = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            preview,
            currentRead,
            new[] { TargetDisplay, SourceDisplay },
            new DesktopIconPositioningCapabilities(
                false,
                Spacing,
                ConnectionMethod.FindWindowSW,
                null),
            new DesktopLayoutSnapshotFactory());

        Assert.True(build.IsSuccess, string.Join("; ", build.Issues.Select(issue => issue.Message)));
        var candidate = Assert.IsType<DesktopLayoutRuleAwareExecutionCandidate>(build.Candidate);
        Assert.True(candidate.IsExecutable);
        Assert.False(candidate.IsNoOp);
        Assert.Equal(1, candidate.MoveRequiredCount);
        Assert.Equal(TargetDisplay.DeviceName, candidate.TargetDeviceName);

        var restoreItem = Assert.Single(candidate.RestorePlan.Items);
        Assert.Equal(TargetDisplayLayoutRestoreItemState.MoveRequired, restoreItem.State);
        Assert.Equal(SourceDisplay.DeviceName, restoreItem.CurrentMonitorDeviceName);
        Assert.Equal(icon.PixelPosition, restoreItem.CurrentPosition);

        var targetSnapshotIcon = Assert.Single(candidate.TargetSnapshot.Icons);
        Assert.Equal(TargetDisplay.DeviceName, targetSnapshotIcon.MonitorDeviceName);
        Assert.Equal(previewItem.TargetPosition, targetSnapshotIcon.PixelPosition);

        var step = Assert.Single(candidate.ExecutionPlan.Steps);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, step.Kind);
        Assert.Equal(icon.PixelPosition, step.FromPosition);
        Assert.Equal(previewItem.TargetPosition, step.ToPosition);
        Assert.Equal(TargetDisplay.DeviceName, step.ExpectedMonitorDeviceName);
    }

    [Fact]
    public void SameOutOfScopeOverride_WithoutExplicitInbound_DoesNotCreateCrossDisplayMove()
    {
        var templateId = Guid.NewGuid();
        var zone = new DesktopLayoutZone(
            Guid.NewGuid(),
            "主屏目标区",
            new NormalizedRect(0, 0, 10000, 10000),
            0,
            ZonePlacementFlow.ColumnMajor,
            null);
        var template = new DesktopLayoutTemplate(
            templateId,
            "保持屏幕归属",
            1,
            TargetDisplayBinding.ForExactDeviceName(TargetDisplay.DeviceName),
            new[] { zone });
        var icon = SnapshotIcon(
            0,
            "cross",
            new PixelPoint(-1000, 80),
            SourceDisplay.DeviceName);
        var ruleSet = new DesktopLayoutIconRuleSet(
            templateId,
            new[] { DesktopLayoutIconRule.ForZoneOverride(icon.StableId, zone.ZoneId) });

        var preview = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            ruleSet,
            new[] { TargetDisplay, SourceDisplay },
            null,
            Spacing,
            new[] { icon });

        Assert.Equal(0, preview.InboundMigrationIconCount);
        Assert.Equal(0, preview.ManagedTargetIconCount);
        Assert.Equal(1, preview.OutOfScopeIconCount);
        Assert.Equal(0, preview.MoveRequiredCount);
        Assert.False(preview.HasCrossDisplayChanges);
    }

    private static DesktopIconSnapshotItem SnapshotIcon(
        int ordinal,
        string stableId,
        PixelPoint position,
        string monitor)
        => new(
            ordinal,
            stableId,
            stableId,
            null,
            ItemKind.File,
            position,
            monitor,
            false,
            true);

    private static DesktopIconReadResult Read(params DesktopIconSnapshotItem[] icons)
        => new(
            icons.Select(icon => new DesktopIconDescriptor(
                icon.StableId,
                icon.DisplayName,
                icon.ParsingName,
                icon.ItemKind,
                icon.PixelPosition,
                icon.MonitorDeviceName,
                icon.IsSystemItem,
                icon.IsFileSystemItem)).ToArray(),
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);
}
