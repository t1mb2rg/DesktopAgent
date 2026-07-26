using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Tests.DesktopLayouts.Templates;

public sealed class DesktopLayoutTemplateTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);

    private static DesktopLayoutZone Z(Guid id, string name, NormalizedRect bounds,
        int sortOrder, int? maxItemCount = null) =>
        new(id, name, bounds, sortOrder, ZonePlacementFlow.ColumnMajor, maxItemCount);

    private static DesktopLayoutTemplate Template(
        Guid? templateId = null,
        string name = "Test Template",
        int version = 1,
        TargetDisplayBinding? binding = null,
        IEnumerable<DesktopLayoutZone>? zones = null) =>
        new(templateId ?? Guid.NewGuid(), name, version,
            binding ?? TargetDisplayBinding.ForSelectedTarget(),
            zones ?? new[] { Z(Guid.NewGuid(), "A", R(0, 0, 5000, 5000), 0) });

    // ═══ 正常构造 ═══

    [Fact]
    public void ValidTemplate_ConstructsSuccessfully()
    {
        var t = Template();
        Assert.Equal("Test Template", t.Name);
        Assert.Equal(1, t.Version);
        Assert.Single(t.Zones);
    }

    // ═══ TemplateId 验证 ═══

    [Fact]
    public void EmptyTemplateId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Template(templateId: Guid.Empty));
        Assert.Contains("TemplateId", ex.Message);
    }

    // ═══ Name 验证 ═══

    [Fact]
    public void NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Template(name: null!));
    }

    [Fact]
    public void WhitespaceName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Template(name: "   "));
        Assert.Contains("Name", ex.Message);
    }

    // ═══ Version 验证 ═══

    [Fact]
    public void ZeroVersion_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Template(version: 0));
        Assert.Contains("Version", ex.Message);
    }

    [Fact]
    public void NegativeVersion_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Template(version: -1));
        Assert.Contains("Version", ex.Message);
    }

    // ═══ Binding 验证 ═══

    [Fact]
    public void NullBinding_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DesktopLayoutTemplate(
                Guid.NewGuid(), "T", 1, null!,
                new[] { Z(Guid.NewGuid(), "A", R(0, 0, 5000, 5000), 0) }));
    }

    // ═══ Zones 验证 ═══

    [Fact]
    public void NullZones_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DesktopLayoutTemplate(
                Guid.NewGuid(), "T", 1,
                TargetDisplayBinding.ForSelectedTarget(), null!));
    }

    [Fact]
    public void EmptyZones_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Template(zones: Array.Empty<DesktopLayoutZone>()));
        Assert.Contains("Zones", ex.Message);
    }

    [Fact]
    public void NullZoneItem_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Template(zones: new DesktopLayoutZone[] { null! }));
        Assert.Contains("null", ex.Message.ToLowerInvariant());
    }

    // ═══ 重复 ZoneId ═══

    [Fact]
    public void DuplicateZoneId_Throws()
    {
        var dup = Guid.NewGuid();
        var ex = Assert.Throws<ArgumentException>(
            () => Template(zones: new[]
            {
                Z(dup, "A", R(0, 0, 3000, 5000), 0),
                Z(dup, "B", R(4000, 0, 7000, 5000), 1),
            }));
        Assert.Contains(dup.ToString(), ex.Message);
    }

    // ═══ 重复 SortOrder ═══

    [Fact]
    public void DuplicateSortOrder_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Template(zones: new[]
            {
                Z(Guid.NewGuid(), "A", R(0, 0, 3000, 5000), 0),
                Z(Guid.NewGuid(), "B", R(4000, 0, 7000, 5000), 0),
            }));
        Assert.Contains("SortOrder", ex.Message);
    }

    // ═══ 重叠分区 ═══

    [Fact]
    public void OverlappingZones_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Template(zones: new[]
            {
                Z(Guid.NewGuid(), "A", R(0, 0, 5000, 5000), 0),
                Z(Guid.NewGuid(), "B", R(4000, 4000, 9000, 9000), 1),
            }));
        Assert.Contains("重叠", ex.Message);
    }

    // ═══ 相邻分区允许 ═══

    [Fact]
    public void HorizontallyAdjacentZones_AreAllowed()
    {
        var t = Template(zones: new[]
        {
            Z(Guid.NewGuid(), "Left",  R(0, 0, 5000, 10000), 0),
            Z(Guid.NewGuid(), "Right", R(5000, 0, 10000, 10000), 1),
        });
        Assert.Equal(2, t.Zones.Count);
    }

    [Fact]
    public void VerticallyAdjacentZones_AreAllowed()
    {
        var t = Template(zones: new[]
        {
            Z(Guid.NewGuid(), "Top",    R(0, 0, 10000, 5000), 0),
            Z(Guid.NewGuid(), "Bottom", R(0, 5000, 10000, 10000), 1),
        });
        Assert.Equal(2, t.Zones.Count);
    }

    // ═══ 分区间留空允许 ═══

    [Fact]
    public void GapsBetweenZones_AreAllowed()
    {
        var t = Template(zones: new[]
        {
            Z(Guid.NewGuid(), "Left",  R(0, 0, 3000, 10000), 0),
            Z(Guid.NewGuid(), "Right", R(7000, 0, 10000, 10000), 1),
        });
        Assert.Equal(2, t.Zones.Count);
    }

    // ═══ 保持输入顺序 ═══

    [Fact]
    public void Zones_PreserveInputOrder()
    {
        var zones = new[]
        {
            Z(Guid.NewGuid(), "C", R(8000, 0, 10000, 5000), 2),
            Z(Guid.NewGuid(), "A", R(0, 0, 4000, 5000), 0),
            Z(Guid.NewGuid(), "B", R(5000, 0, 7000, 5000), 1),
        };
        var t = Template(zones: zones);
        Assert.Equal("C", t.Zones[0].Name);
        Assert.Equal("A", t.Zones[1].Name);
        Assert.Equal("B", t.Zones[2].Name);
    }

    // ═══ 不可变性 ═══

    [Fact]
    public void Zones_IsReadOnly()
    {
        var t = Template();
        Assert.Throws<NotSupportedException>(
            () => ((IList<DesktopLayoutZone>)t.Zones).Clear());
    }

    [Fact]
    public void MutateOriginalList_DoesNotAffectTemplate()
    {
        var zones = new List<DesktopLayoutZone>
        {
            Z(Guid.NewGuid(), "A", R(0, 0, 5000, 5000), 0),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 5000), 1),
        };
        var t = Template(zones: zones);

        zones.Clear(); // modify original

        Assert.Equal(2, t.Zones.Count); // template unaffected
        Assert.Equal("A", t.Zones[0].Name);
    }

    // ═══ 相同 Name 不同 ID 合法 ═══

    [Fact]
    public void SameNameDifferentId_IsAllowed()
    {
        var t = Template(zones: new[]
        {
            Z(Guid.NewGuid(), "Same", R(0, 0, 3000, 5000), 0),
            Z(Guid.NewGuid(), "Same", R(4000, 0, 7000, 5000), 1),
        });
        Assert.Equal(2, t.Zones.Count);
    }
}
