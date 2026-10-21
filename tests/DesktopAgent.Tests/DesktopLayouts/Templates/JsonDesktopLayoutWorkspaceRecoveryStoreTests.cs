using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Templates;

namespace DesktopAgent.Tests.DesktopLayouts.Templates;

public sealed class JsonDesktopLayoutWorkspaceRecoveryStoreTests
{
    [Fact]
    public async Task RoundTrip_PreservesDualDisplayDraftsInboundAndMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tessel-workspace-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "workspace.json");
        try
        {
            var primaryId = Guid.NewGuid();
            var secondaryId = Guid.NewGuid();
            var primaryZone = Guid.NewGuid();
            var secondaryZone = Guid.NewGuid();
            const string inboundId = "inbound-item";

            var primary = Draft(
                Display(@"\\.\DISPLAY1", isPrimary: true, top: 0),
                "primary_1",
                primaryId,
                primaryZone,
                new[] { DesktopLayoutIconRule.ForExclude(inboundId) });
            var secondary = Draft(
                Display(@"\\.\DISPLAY2", isPrimary: false, top: 1080),
                "secondary_1",
                secondaryId,
                secondaryZone,
                new[] { DesktopLayoutIconRule.ForZoneOverride(inboundId, secondaryZone) },
                new[] { inboundId });
            var migration = new DesktopLayoutWorkspaceMigrationRecoveryState(
                inboundId,
                primary.Display.DeviceName,
                secondary.Display.DeviceName,
                secondaryZone,
                DesktopLayoutIconRule.ForZoneOverride(inboundId, primaryZone));
            var expected = new DesktopLayoutWorkspaceRecoveryState(
                secondary.Display.DeviceName,
                new[] { primary, secondary },
                new[] { migration });

            var store = new JsonDesktopLayoutWorkspaceRecoveryStore(path);
            await store.SaveAsync(expected);
            var actual = Assert.IsType<DesktopLayoutWorkspaceRecoveryState>(await store.LoadAsync());

            Assert.Equal(secondary.Display.DeviceName, actual.ActiveDeviceName);
            Assert.Equal(2, actual.Displays.Count);
            var restoredSecondary = Assert.Single(actual.Displays, item =>
                item.Display.DeviceName == secondary.Display.DeviceName);
            Assert.Equal(new[] { inboundId }, restoredSecondary.ExplicitInboundStableIds);
            Assert.Equal(secondaryZone, Assert.Single(restoredSecondary.RuleSet.Rules).TargetZoneId);
            Assert.Equal(1080, restoredSecondary.Display.Bounds.Top);
            Assert.Single(actual.Migrations);
            Assert.Equal(primary.Display.DeviceName, actual.Migrations[0].SourceDeviceName);

            await store.DeleteAsync();
            Assert.Null(await store.LoadAsync());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_InvalidJson_FailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tessel-workspace-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "workspace.json");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(path, "not-json");
            var store = new JsonDesktopLayoutWorkspaceRecoveryStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static DesktopLayoutWorkspaceDraftRecoveryState Draft(
        DisplayDescriptor display,
        string displayRef,
        Guid templateId,
        Guid zoneId,
        IReadOnlyList<DesktopLayoutIconRule> rules,
        IReadOnlyCollection<string>? inbound = null)
        => new(
            display,
            displayRef,
            observedIconCount: 10,
            sourceAssignmentCount: 10,
            mappedAssignmentCount: 10,
            templateId,
            sourceVersion: 0,
            name: $"AI 建议布局 · {display.DeviceName}",
            zones: new[]
            {
                new DesktopLayoutZone(
                    zoneId,
                    "工作区",
                    new NormalizedRect(0, 0, 10000, 10000),
                    0,
                    ZonePlacementFlow.ColumnMajor,
                    10),
            },
            ruleSet: new DesktopLayoutIconRuleSet(templateId, rules),
            explicitInboundStableIds: inbound);

    private static DisplayDescriptor Display(string deviceName, bool isPrimary, int top)
        => new(
            deviceName,
            new PixelRect(0, top, 1920, top + 1080),
            new PixelRect(0, top, 1920, top + 1040),
            96,
            96,
            isPrimary);
}
