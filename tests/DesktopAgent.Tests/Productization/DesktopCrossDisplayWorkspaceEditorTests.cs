using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopCrossDisplayWorkspaceEditorTests
{
    [Fact]
    public void Reassign_MovesExplicitRuleToTarget_AndExcludesSource()
    {
        var sourceZone = Zone("源区", 0);
        var targetZone = Zone("目标区", 0);
        var source = Draft(
            @"\\.\DISPLAY1",
            sourceZone,
            DesktopLayoutIconRule.ForZoneOverride("icon-a", sourceZone.ZoneId),
            DesktopLayoutIconRule.ForZoneOverride("keep-source", sourceZone.ZoneId));
        var target = Draft(
            @"\\.\DISPLAY2",
            targetZone,
            DesktopLayoutIconRule.ForZoneOverride("keep-target", targetZone.ZoneId));

        var result = DesktopCrossDisplayWorkspaceEditor.Reassign(
            new[] { source, target },
            existingMigrations: null,
            stableId: "icon-a",
            sourceDeviceName: @"\\.\DISPLAY1",
            targetDeviceName: @"\\.\DISPLAY2",
            targetZoneId: targetZone.ZoneId);

        var updatedSource = Assert.Single(result.Drafts, draft =>
            draft.TargetDisplayBinding.DeviceName == @"\\.\DISPLAY1");
        var updatedTarget = Assert.Single(result.Drafts, draft =>
            draft.TargetDisplayBinding.DeviceName == @"\\.\DISPLAY2");

        var sourceRule = Assert.Single(updatedSource.RuleSet.Rules, rule => rule.StableId == "icon-a");
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, sourceRule.Kind);
        Assert.Null(sourceRule.TargetZoneId);
        Assert.Contains(updatedSource.RuleSet.Rules, rule => rule.StableId == "keep-source");
        Assert.Empty(updatedSource.ExplicitInboundStableIds);

        var targetRule = Assert.Single(updatedTarget.RuleSet.Rules, rule => rule.StableId == "icon-a");
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, targetRule.Kind);
        Assert.Equal(targetZone.ZoneId, targetRule.TargetZoneId);
        Assert.Contains(updatedTarget.RuleSet.Rules, rule => rule.StableId == "keep-target");
        Assert.Equal(new[] { "icon-a" }, updatedTarget.ExplicitInboundStableIds);

        var migration = Assert.Single(result.Migrations);
        Assert.Equal("icon-a", migration.StableId);
        Assert.Equal(@"\\.\DISPLAY1", migration.SourceDeviceName);
        Assert.Equal(@"\\.\DISPLAY2", migration.TargetDeviceName);
        Assert.Equal(targetZone.ZoneId, migration.TargetZoneId);
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, migration.SourceRuleBeforeMigration.Kind);

        Assert.Contains("icon-a", result.GetExplicitInboundStableIds(@"\\.\DISPLAY2"));
        Assert.Empty(result.GetExplicitInboundStableIds(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void CompleteTargetMigrations_ClearsPendingAuthorization_ButKeepsStableRules()
    {
        var sourceZone = Zone("源区", 0);
        var targetZone = Zone("目标区", 0);
        var source = Draft(
            @"\\.\DISPLAY1",
            sourceZone,
            DesktopLayoutIconRule.ForZoneOverride("icon-a", sourceZone.ZoneId));
        var target = Draft(@"\\.\DISPLAY2", targetZone);

        var pending = DesktopCrossDisplayWorkspaceEditor.Reassign(
            new[] { source, target },
            null,
            "icon-a",
            @"\\.\DISPLAY1",
            @"\\.\DISPLAY2",
            targetZone.ZoneId);

        var committed = DesktopCrossDisplayWorkspaceEditor.CompleteTargetMigrations(
            pending.Drafts,
            pending.Migrations,
            @"\\.\DISPLAY2",
            new[] { "icon-a" });

        Assert.Empty(committed.Migrations);

        var committedSource = Assert.Single(committed.Drafts, draft =>
            draft.TargetDisplayBinding.DeviceName == @"\\.\DISPLAY1");
        var committedTarget = Assert.Single(committed.Drafts, draft =>
            draft.TargetDisplayBinding.DeviceName == @"\\.\DISPLAY2");

        Assert.Empty(committedSource.ExplicitInboundStableIds);
        Assert.Empty(committedTarget.ExplicitInboundStableIds);
        Assert.Equal(
            DesktopLayoutIconRuleKind.Exclude,
            Assert.Single(committedSource.RuleSet.Rules, rule => rule.StableId == "icon-a").Kind);
        var targetRule = Assert.Single(committedTarget.RuleSet.Rules, rule => rule.StableId == "icon-a");
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, targetRule.Kind);
        Assert.Equal(targetZone.ZoneId, targetRule.TargetZoneId);
    }

    [Fact]
    public void CompleteTargetMigrations_RejectsWrongTargetDisplay()
    {
        var sourceZone = Zone("源区", 0);
        var targetZone = Zone("目标区", 0);
        var pending = DesktopCrossDisplayWorkspaceEditor.Reassign(
            new[]
            {
                Draft(@"\\.\DISPLAY1", sourceZone,
                    DesktopLayoutIconRule.ForZoneOverride("icon-a", sourceZone.ZoneId)),
                Draft(@"\\.\DISPLAY2", targetZone),
            },
            null,
            "icon-a",
            @"\\.\DISPLAY1",
            @"\\.\DISPLAY2",
            targetZone.ZoneId);

        Assert.Throws<InvalidOperationException>(() =>
            DesktopCrossDisplayWorkspaceEditor.CompleteTargetMigrations(
                pending.Drafts,
                pending.Migrations,
                @"\\.\DISPLAY1",
                new[] { "icon-a" }));
    }

    [Fact]
    public void Reassign_RejectsSecondPendingMigrationForSameStableId()
    {
        var z1 = Zone("D1", 0);
        var z2 = Zone("D2", 0);
        var z3 = Zone("D3", 0);
        var d1 = Draft(@"\\.\DISPLAY1", z1,
            DesktopLayoutIconRule.ForZoneOverride("icon-a", z1.ZoneId));
        var d2 = Draft(@"\\.\DISPLAY2", z2);
        var d3 = Draft(@"\\.\DISPLAY3", z3);

        var first = DesktopCrossDisplayWorkspaceEditor.Reassign(
            new[] { d1, d2, d3 }, null,
            "icon-a", @"\\.\DISPLAY1", @"\\.\DISPLAY2", z2.ZoneId);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopCrossDisplayWorkspaceEditor.Reassign(
                first.Drafts,
                first.Migrations,
                "icon-a", @"\\.\DISPLAY2", @"\\.\DISPLAY3", z3.ZoneId));

        Assert.Contains("待执行跨屏迁移", ex.Message, StringComparison.Ordinal);
        Assert.Contains(@"\\.\DISPLAY1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(@"\\.\DISPLAY2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reassign_RejectsPinOrExcludeSource_InsteadOfSilentlyChangingAuthority()
    {
        var z1 = Zone("D1", 0);
        var z2 = Zone("D2", 0);
        var source = Draft(@"\\.\DISPLAY1", z1,
            DesktopLayoutIconRule.ForPin("icon-a"));
        var target = Draft(@"\\.\DISPLAY2", z2);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopCrossDisplayWorkspaceEditor.Reassign(
                new[] { source, target }, null,
                "icon-a", @"\\.\DISPLAY1", @"\\.\DISPLAY2", z2.ZoneId));

        Assert.Contains("Pin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reassign_RejectsUnknownTargetZone()
    {
        var z1 = Zone("D1", 0);
        var z2 = Zone("D2", 0);
        var source = Draft(@"\\.\DISPLAY1", z1,
            DesktopLayoutIconRule.ForZoneOverride("icon-a", z1.ZoneId));
        var target = Draft(@"\\.\DISPLAY2", z2);

        Assert.Throws<ArgumentException>(() =>
            DesktopCrossDisplayWorkspaceEditor.Reassign(
                new[] { source, target }, null,
                "icon-a", @"\\.\DISPLAY1", @"\\.\DISPLAY2", Guid.NewGuid()));
    }

    [Fact]
    public void CrossDisplayEditor_RemainsApplicationOnly_WithoutWriterAuthority()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "DesktopAgent.Application", "DesktopLayouts", "Templates", "Editing",
            "DesktopCrossDisplayWorkspaceEditor.cs"));

        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDesktopIcon", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetDisplayLayoutExecutionCoordinator", source, StringComparison.Ordinal);
    }

    private static DesktopLayoutEditDraft Draft(
        string deviceName,
        DesktopLayoutZone zone,
        params DesktopLayoutIconRule[] rules)
    {
        var templateId = Guid.NewGuid();
        return new DesktopLayoutEditDraft(
            templateId,
            0,
            $"布局 {deviceName}",
            TargetDisplayBinding.ForExactDeviceName(deviceName),
            new[] { zone },
            new DesktopLayoutIconRuleSet(templateId, rules));
    }

    private static DesktopLayoutZone Zone(string name, int sortOrder)
        => new(
            Guid.NewGuid(),
            name,
            new NormalizedRect(0, 0, 10000, 10000),
            sortOrder,
            ZonePlacementFlow.ColumnMajor,
            null);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("找不到 DesktopAgent.sln，无法定位源码合同测试根目录。");
    }
}
