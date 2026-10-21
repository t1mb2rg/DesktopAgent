using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Execution;

public sealed class DesktopLayoutRuleAwareExecutionCandidateFixedItemsTests
{
    [Fact]
    public void ExcludedSystemAndFolder_DoNotEnterTargetSnapshot_OrBlockSingleMove()
    {
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var display = new DisplayDescriptor(
            "DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1080),
            96, 96, true);
        var spacing = new PixelPoint(80, 80);
        var template = new DesktopLayoutTemplate(
            templateId,
            "Acceptance",
            1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[]
            {
                new DesktopLayoutZone(
                    zoneId,
                    "Z",
                    new NormalizedRect(0, 0, 10000, 10000),
                    0,
                    ZonePlacementFlow.ColumnMajor,
                    maxItemCount: 2),
            });

        var a = new DesktopIconSnapshotItem(
            0, "a", "A", null, ItemKind.File,
            new PixelPoint(80, 0), "DISPLAY1", false, true);
        var b = new DesktopIconSnapshotItem(
            1, "b", "B", null, ItemKind.File,
            new PixelPoint(0, 80), "DISPLAY1", false, true);
        var recycleBin = new DesktopIconSnapshotItem(
            2, "recycle", "Recycle Bin", null, ItemKind.SystemItem,
            new PixelPoint(400, 400), "DISPLAY1", true, false);
        var folder = new DesktopIconSnapshotItem(
            3, "folder", "icons", null, ItemKind.Folder,
            new PixelPoint(480, 400), "DISPLAY1", false, true);
        var input = new[] { a, b, recycleBin, folder };

        var rules = new DesktopLayoutIconRuleSet(
            templateId,
            new[]
            {
                DesktopLayoutIconRule.ForExclude("recycle"),
                DesktopLayoutIconRule.ForExclude("folder"),
            });

        var preview = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            rules,
            new[] { display },
            null,
            spacing,
            input);

        Assert.True(preview.IsComplete);
        Assert.Equal(2, preview.ExcludedCount);
        Assert.Equal(1, preview.MoveRequiredCount);
        Assert.Equal(1, preview.AlreadyAtTargetCount);

        var read = new DesktopIconReadResult(
            input.Select(icon => new DesktopIconDescriptor(
                icon.StableId,
                icon.DisplayName,
                icon.ParsingName,
                icon.ItemKind,
                icon.PixelPosition,
                icon.MonitorDeviceName,
                icon.IsSystemItem,
                icon.IsFileSystemItem)).ToList(),
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

        var result = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            preview,
            read,
            new[] { display },
            new DesktopIconPositioningCapabilities(
                false, spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());

        Assert.True(result.IsSuccess);
        var candidate = Assert.IsType<DesktopLayoutRuleAwareExecutionCandidate>(result.Candidate);
        Assert.Equal(2, candidate.FixedCount);
        Assert.Equal(1, candidate.MoveRequiredCount);
        Assert.Equal(0, candidate.RestoreBlockedCount);
        Assert.Equal(0, candidate.RestoreMissingCount);
        Assert.Equal(1, candidate.ExecutionStepCount);
        Assert.False(candidate.UsesTemporaryStaging);
        Assert.True(candidate.IsExecutable);
        Assert.True(candidate.RequiresUserConfirmation);

        Assert.Equal(new[] { "a", "b" }, candidate.TargetSnapshot.Icons.Select(i => i.StableId));
        Assert.DoesNotContain(candidate.TargetSnapshot.Icons, i => i.StableId == "recycle");
        Assert.DoesNotContain(candidate.TargetSnapshot.Icons, i => i.StableId == "folder");
    }
}
