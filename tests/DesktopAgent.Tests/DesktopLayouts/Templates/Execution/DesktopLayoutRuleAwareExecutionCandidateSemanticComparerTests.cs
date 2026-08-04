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

public sealed class DesktopLayoutRuleAwareExecutionCandidateSemanticComparerTests
{
    private static readonly Guid _tid = Guid.NewGuid();
    private static readonly Guid _zid = Guid.NewGuid();
    private static readonly PixelPoint _spacing = new(80, 80);
    private static readonly DisplayDescriptor _dd = new("DISPLAY1",
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96, 96, true);

    private static DesktopIconSnapshotItem I(int ord, string sid, PixelPoint? pos = null,
        string? mon = "DISPLAY1")
        => new(ord, sid, $"N_{sid}", null, ItemKind.File,
            pos ?? new PixelPoint(ord * 80, 0), mon, false, true);

    private static DesktopIconDescriptor D(string sid, PixelPoint pos,
        string? mon = "DISPLAY1")
        => new(sid, $"N_{sid}", null, ItemKind.File, pos, mon, false, true);

    private (DesktopLayoutRuleAwareExecutionCandidate, DesktopIconReadResult, IReadOnlyList<DisplayDescriptor>,
        DesktopIconPositioningCapabilities) BuildCandidate(
            DesktopLayoutTemplate? template = null,
            DesktopLayoutIconRuleSet? ruleSet = null,
            string targetDeviceName = "DISPLAY1",
            PixelPoint? spacing = null,
            params DesktopIconSnapshotItem[] icons)
    {
        var t = template ?? new DesktopLayoutTemplate(_tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(_zid, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var rs = ruleSet ?? DesktopLayoutIconRuleSet.Empty(t.TemplateId);
        var sp = spacing ?? _spacing;
        var dds = new[] { _dd };
        var caps = new DesktopIconPositioningCapabilities(false, sp, ConnectionMethod.FindWindowSW, null);
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(t, rs, dds, null, sp, icons);
        var descriptors = icons.Select(i => new DesktopIconDescriptor(
            i.StableId, i.DisplayName, i.ParsingName, i.ItemKind,
            i.PixelPosition, i.MonitorDeviceName, i.IsSystemItem,
            i.IsFileSystemItem)).ToList();
        var read = new DesktopIconReadResult(descriptors, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var sf = new DesktopLayoutSnapshotFactory();
        var result = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(plan, read, dds, caps, sf);
        return (result.Candidate!, read, dds, caps);
    }

    [Fact] public void SameSemanticsDifferentInstance_IsEquivalent()
    {
        var (c1, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));
        var (c2, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.True(result.IsEquivalent);
        Assert.Empty(result.Differences);
    }

    [Fact] public void SnapshotIdChange_StillEquivalent()
    {
        var (c1, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));
        var (c2, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));

        // SnapshotId 和 CreatedAtUtc 不同，但语义应等价
        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.True(result.IsEquivalent);
    }

    [Fact] public void CurrentPositionChanged_IsNotEquivalent()
    {
        var (c1, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));
        var (c2, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(160, 0)));

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
        Assert.Contains(result.Differences, d => d.Contains("PixelPosition"));
    }

    [Fact] public void MetadataChanged_IsNotEquivalent()
    {
        // Two templates with different IDs → different TemplateId comparison fails
        var t1 = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });

        var (c1, _, _, _) = BuildCandidate(template: t1,
            icons: I(0, "a", pos: new PixelPoint(80, 0)));

        var t2 = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var (c2, _, _, _) = BuildCandidate(template: t2,
            icons: I(0, "a", pos: new PixelPoint(80, 0)));

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
    }

    [Fact] public void TemplateVersionChanged_IsNotEquivalent()
    {
        var t1 = new DesktopLayoutTemplate(_tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(_zid, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var t2 = new DesktopLayoutTemplate(_tid, "T", 2,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(_zid, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });

        var (c1, _, _, _) = BuildCandidate(template: t1,
            icons: I(0, "a", pos: new PixelPoint(80, 0)));
        var (c2, _, _, _) = BuildCandidate(template: t2,
            icons: I(0, "a", pos: new PixelPoint(80, 0)));

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
        Assert.Contains(result.Differences, d => d.Contains("Version"));
    }

    [Fact] public void TargetDeviceNameChanged_IsNotEquivalent()
    {
        // Two displays with different device names
        var dd1 = new DisplayDescriptor("DEV_A", new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1080), 96, 96, true);
        var dd2 = new DisplayDescriptor("DEV_B", new PixelRect(1920, 0, 3840, 1080),
            new PixelRect(1920, 0, 3840, 1080), 96, 96, false);

        var t1 = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForExactDeviceName("DEV_A"),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var t2 = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForExactDeviceName("DEV_B"),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var rs1 = DesktopLayoutIconRuleSet.Empty(t1.TemplateId);
        var rs2 = DesktopLayoutIconRuleSet.Empty(t2.TemplateId);
        var sp = _spacing;
        var caps = new DesktopIconPositioningCapabilities(false, sp, ConnectionMethod.FindWindowSW, null);
        var sf = new DesktopLayoutSnapshotFactory();

        // c1: icon on DEV_A
        var icons1 = new[] { I(0, "a", pos: new PixelPoint(80, 0), mon: "DEV_A") };
        var plan1 = DesktopLayoutRuleAwarePreviewPlanner.Plan(t1, rs1, new[] { dd1 }, null, sp, icons1);
        var descs1 = icons1.Select(i => D(i.StableId, i.PixelPosition, i.MonitorDeviceName)).ToList();
        var read1 = new DesktopIconReadResult(descs1, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var r1 = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(plan1, read1, new[] { dd1 }, caps, sf);
        var c1 = r1.Candidate!;

        // c2: icon on DEV_B (different target)
        var icons2 = new[] { I(0, "a", pos: new PixelPoint(2000, 0), mon: "DEV_B") };
        var plan2 = DesktopLayoutRuleAwarePreviewPlanner.Plan(t2, rs2, new[] { dd2 }, null, sp, icons2);
        var descs2 = icons2.Select(i => D(i.StableId, i.PixelPosition, i.MonitorDeviceName)).ToList();
        var read2 = new DesktopIconReadResult(descs2, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var r2 = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(plan2, read2, new[] { dd2 }, caps, sf);
        var c2 = r2.Candidate!;

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
        Assert.Contains(result.Differences, d => d.Contains("TargetDeviceName"));
    }

    [Fact] public void IconSpacingChanged_IsNotEquivalent()
    {
        var (c1, _, _, _) = BuildCandidate(
            spacing: new PixelPoint(80, 80),
            icons: I(0, "a", pos: new PixelPoint(80, 0)));
        var (c2, _, _, _) = BuildCandidate(
            spacing: new PixelPoint(90, 90),
            icons: I(0, "a", pos: new PixelPoint(80, 0)));

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
        Assert.Contains(result.Differences, d => d.Contains("IconSpacing"));
    }

    [Fact] public void RestoreItemStateChanged_IsNotEquivalent()
    {
        // AlreadyAtTarget for c1, MoveRequired for c2 (different position)
        var (c1, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(0, 0)));
        var (c2, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
        Assert.Contains(result.Differences, d => d.Contains("State") || d.Contains("Position"));
    }

    [Fact] public void StepFieldsChanged_IsNotEquivalent()
    {
        var (c1, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));
        // Different icon position → different step
        var (c2, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(160, 0)));

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
        Assert.NotEmpty(result.Differences);
    }

    [Fact] public void StagingStateChanged_IsNotEquivalent()
    {
        // Two icons swapped (cycle) → UsesTemporaryStaging
        var display = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 240, 80),
            new PixelRect(0, 0, 240, 80), 96, 96, true);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var rs = DesktopLayoutIconRuleSet.Empty(t.TemplateId);
        var sp = _spacing;
        var caps = new DesktopIconPositioningCapabilities(false, sp, ConnectionMethod.FindWindowSW, null);

        // No-cycle: sequential dependency chain
        var icons1 = new[] { I(0, "a", pos: new PixelPoint(80, 0)), I(1, "b", pos: new PixelPoint(160, 0)) };
        var plan1 = DesktopLayoutRuleAwarePreviewPlanner.Plan(t, rs, new[] { display }, null, sp, icons1);
        var descs1 = icons1.Select(i => new DesktopIconDescriptor(
            i.StableId, i.DisplayName, i.ParsingName, i.ItemKind,
            i.PixelPosition, i.MonitorDeviceName, i.IsSystemItem, i.IsFileSystemItem)).ToList();
        var read1 = new DesktopIconReadResult(descs1, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var sf = new DesktopLayoutSnapshotFactory();
        var r1 = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(plan1, read1, new[] { display }, caps, sf);
        var c1 = r1.Candidate!;

        // Cycle: swapped positions
        var icons2 = new[] { I(0, "a", pos: new PixelPoint(80, 0)), I(1, "b", pos: new PixelPoint(0, 0)) };
        var plan2 = DesktopLayoutRuleAwarePreviewPlanner.Plan(t, rs, new[] { display }, null, sp, icons2);
        var descs2 = icons2.Select(i => new DesktopIconDescriptor(
            i.StableId, i.DisplayName, i.ParsingName, i.ItemKind,
            i.PixelPosition, i.MonitorDeviceName, i.IsSystemItem, i.IsFileSystemItem)).ToList();
        var read2 = new DesktopIconReadResult(descs2, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var r2 = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(plan2, read2, new[] { display }, caps, sf);
        var c2 = r2.Candidate!;

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
    }

    [Fact] public void Differences_HaveReadableInfo()
    {
        var (c1, _, _, _) = BuildCandidate(
            icons: I(0, "a", pos: new PixelPoint(80, 0)));
        var (c2, _, _, _) = BuildCandidate(
            icons: I(0, "b", pos: new PixelPoint(80, 0))); // Different StableId

        var result = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(c1, c2);
        Assert.False(result.IsEquivalent);
        Assert.NotEmpty(result.Differences);
        // All differences should contain StableId or descriptive text
        foreach (var d in result.Differences)
            Assert.True(d.Contains("StableId") || d.Contains("→") || d.Contains("Count"));
    }
}
