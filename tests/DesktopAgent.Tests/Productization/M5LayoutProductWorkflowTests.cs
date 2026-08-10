using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.Productization;

public sealed class M5LayoutProductWorkflowTests
{
    [Theory]
    [InlineData(DesktopLayoutPresetKind.Full, 1)]
    [InlineData(DesktopLayoutPresetKind.TwoColumns, 2)]
    [InlineData(DesktopLayoutPresetKind.ThreeColumns, 3)]
    [InlineData(DesktopLayoutPresetKind.FourGrid, 4)]
    public void Presets_CreateValidNonOverlappingDrafts(
        DesktopLayoutPresetKind preset,
        int expectedZoneCount)
    {
        var draft = DesktopLayoutPresetFactory.CreateNewDraft("Daily", preset);

        Assert.True(draft.IsNew);
        Assert.Equal(expectedZoneCount, draft.Zones.Count);
        Assert.Equal(1, draft.BuildNextPersistedTemplate().Version);
        Assert.Equal(draft.TemplateId, draft.RuleSet.TemplateId);
    }

    [Fact]
    public void PersistedDraft_SaveAdvancesExactlyOneVersion()
    {
        var fresh = DesktopLayoutPresetFactory.CreateNewDraft(
            "Daily",
            DesktopLayoutPresetKind.TwoColumns);
        var persisted = fresh.BuildNextPersistedTemplate();
        var loaded = DesktopLayoutEditDraft.FromPersisted(persisted, fresh.RuleSet);

        var next = loaded.WithMetadata(
            "Daily renamed",
            persisted.TargetDisplayBinding)
            .BuildNextPersistedTemplate();

        Assert.Equal(persisted.TemplateId, next.TemplateId);
        Assert.Equal(2, next.Version);
        Assert.Equal("Daily renamed", next.Name);
    }

    [Fact]
    public void ImpactPreview_IsBoundToFreshObservationAndContainsNoExecutionCommand()
    {
        var draft = DesktopLayoutPresetFactory.CreateNewDraft(
            "Preview",
            DesktopLayoutPresetKind.Full);
        var display = new DisplayDescriptor(
            "DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var observationId = Guid.NewGuid();
        var observedAt = DateTimeOffset.UtcNow;
        var observation = new DesktopObservation(
            observationId,
            observedAt,
            new[] { display },
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            new[]
            {
                new DesktopIconDescriptor(
                    "C:\\Users\\test\\Desktop\\note.txt",
                    "note.txt",
                    "C:\\Users\\test\\Desktop\\note.txt",
                    ItemKind.File,
                    new PixelPoint(0, 0),
                    "DISPLAY1",
                    isSystemItem: false,
                    isFileSystemItem: true),
            },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));

        var preview = DesktopLayoutImpactPreviewService.Create(draft, observation);

        Assert.Equal(observationId, preview.ObservationId);
        Assert.Equal(observedAt, preview.ObservedAtUtc);
        Assert.Single(preview.Plan.InputIcons);
        Assert.Equal("DISPLAY1", preview.Plan.TargetDisplay.DeviceName);
        Assert.DoesNotContain(
            preview.GetType().GetProperties(),
            property => property.Name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }
}
