using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Tests.LayoutTemplates.Editor;

public sealed class DesktopZoneEditOperationsTests
{
    [Fact]
    public void Move_clamps_zone_inside_normalized_desktop()
    {
        var zone = Zone("A", 1000, 2000, 4000, 5000, 0);

        var moved = DesktopZoneEditOperations.Move(zone, -5000, 8000);

        Assert.Equal(0, moved.Bounds.Left);
        Assert.Equal(7000, moved.Bounds.Top);
        Assert.Equal(3000, moved.Bounds.Right);
        Assert.Equal(10000, moved.Bounds.Bottom);
    }

    [Fact]
    public void Resize_bottom_right_respects_minimum_size_and_desktop_edge()
    {
        var zone = Zone("A", 1000, 1000, 5000, 5000, 0);

        var shrunk = DesktopZoneEditOperations.ResizeBottomRight(
            zone,
            -9000,
            -9000,
            minimumWidth: 1200,
            minimumHeight: 900);
        var grown = DesktopZoneEditOperations.ResizeBottomRight(
            zone,
            9000,
            9000,
            minimumWidth: 1200,
            minimumHeight: 900);

        Assert.Equal(new NormalizedRect(1000, 1000, 2200, 1900), shrunk.Bounds);
        Assert.Equal(new NormalizedRect(1000, 1000, 10000, 10000), grown.Bounds);
    }

    [Fact]
    public void Replace_zone_rejects_overlap()
    {
        var first = Zone("A", 0, 0, 5000, 10000, 0);
        var second = Zone("B", 5000, 0, 10000, 10000, 1);
        var overlapping = DesktopZoneEditOperations.Move(second, -1000, 0);

        var error = Assert.Throws<InvalidOperationException>(() =>
            DesktopZoneEditOperations.ReplaceZone([first, second], overlapping));

        Assert.Contains("不能与", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_zone_uses_legal_free_desktop_without_overlap()
    {
        var existing = Zone("A", 0, 0, 5000, 10000, 0);

        var updated = DesktopZoneEditOperations.AddZone([existing]);

        Assert.Equal(2, updated.Count);
        Assert.False(updated[0].Bounds.Overlaps(updated[1].Bounds));
        Assert.Equal(1, updated[1].SortOrder);
    }

    [Fact]
    public void Add_zone_rejects_fully_managed_desktop()
    {
        var full = Zone("A", 0, 0, 10000, 10000, 0);

        Assert.Throws<InvalidOperationException>(() =>
            DesktopZoneEditOperations.AddZone([full]));
    }

    [Fact]
    public void Delete_zone_rejects_last_zone_and_zone_override_reference()
    {
        var templateId = Guid.NewGuid();
        var first = Zone("A", 0, 0, 5000, 10000, 0);
        var second = Zone("B", 5000, 0, 10000, 10000, 1);
        var emptyRules = DesktopLayoutIconRuleSet.Empty(templateId);
        var referencedRules = new DesktopLayoutIconRuleSet(
            templateId,
            [DesktopLayoutIconRule.ForZoneOverride("stable:item", second.ZoneId)]);

        Assert.Throws<InvalidOperationException>(() =>
            DesktopZoneEditOperations.DeleteZone([first], emptyRules, first.ZoneId));
        Assert.Throws<InvalidOperationException>(() =>
            DesktopZoneEditOperations.DeleteZone([first, second], referencedRules, second.ZoneId));

        var remaining = DesktopZoneEditOperations.DeleteZone(
            [first, second],
            emptyRules,
            second.ZoneId);
        Assert.Single(remaining);
        Assert.Equal(first.ZoneId, remaining[0].ZoneId);
    }

    private static DesktopLayoutZone Zone(
        string name,
        int left,
        int top,
        int right,
        int bottom,
        int order)
        => new(
            Guid.NewGuid(),
            name,
            new NormalizedRect(left, top, right, bottom),
            order,
            ZonePlacementFlow.RowMajor,
            maxItemCount: null);
}
