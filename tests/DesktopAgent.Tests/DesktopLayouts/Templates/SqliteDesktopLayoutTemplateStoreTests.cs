using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts.Templates;

public sealed class SqliteDesktopLayoutTemplateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopLayoutTemplateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.Tmpl.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* 尽力清理 */ }
    }

    private SqliteConnection OpenRawConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };
        var c = new SqliteConnection(builder.ConnectionString);
        c.Open();
        return c;
    }

    private SqliteDesktopLayoutTemplateStore CreateStore()
    {
        var store = new SqliteDesktopLayoutTemplateStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    }

    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string name, NormalizedRect bounds,
        int so, int? max = null, ZonePlacementFlow f = ZonePlacementFlow.ColumnMajor) =>
        new(id, name, bounds, so, f, max);

    private static DesktopLayoutTemplate MakeT(Guid? id = null, string name = "Test",
        int ver = 1, TargetDisplayBinding? b = null, List<DesktopLayoutZone>? z = null)
    {
        z ??= new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "Main", R(0, 0, 5000, 10000), 0) };
        return new DesktopLayoutTemplate(id ?? Guid.NewGuid(), name, ver,
            b ?? TargetDisplayBinding.ForSelectedTarget(), z);
    }

    // ═══ Round-trips ═══

    [Fact] public void SaveGet_SingleZone() { var s = CreateStore(); var id = Guid.NewGuid(); var t = MakeT(id: id); s.SaveAsync(t).GetAwaiter().GetResult(); var l = s.GetAsync(id).GetAwaiter().GetResult(); Assert.Equal(id, l!.TemplateId); Assert.Single(l.Zones); }
    [Fact] public void SaveGet_MultiZone() { var s = CreateStore(); var id = Guid.NewGuid(); var t = new DesktopLayoutTemplate(id, "M", 2, TargetDisplayBinding.ForPrimaryDisplay(), new[] { Z(Guid.NewGuid(), "L", R(0,0,5000,10000), 0), Z(Guid.NewGuid(), "R", R(5000,0,10000,10000), 1) }); s.SaveAsync(t).GetAwaiter().GetResult(); var l = s.GetAsync(id).GetAwaiter().GetResult(); Assert.Equal(2, l!.Zones.Count); Assert.Equal(TargetDisplayBindingKind.PrimaryDisplay, l.TargetDisplayBinding.Kind); }
    [Fact] public void AllBindings_RoundTrip() { var s = CreateStore(); var t1 = MakeT(id: Guid.NewGuid(), name: "S", b: TargetDisplayBinding.ForSelectedTarget()); var t2 = MakeT(id: Guid.NewGuid(), name: "P", b: TargetDisplayBinding.ForPrimaryDisplay()); var t3 = MakeT(id: Guid.NewGuid(), name: "E", b: TargetDisplayBinding.ForExactDeviceName(@"\\.\D2")); s.SaveAsync(t1).GetAwaiter().GetResult(); s.SaveAsync(t2).GetAwaiter().GetResult(); s.SaveAsync(t3).GetAwaiter().GetResult(); Assert.Equal(TargetDisplayBindingKind.SelectedTarget, s.GetAsync(t1.TemplateId).GetAwaiter().GetResult()!.TargetDisplayBinding.Kind); Assert.Equal(TargetDisplayBindingKind.PrimaryDisplay, s.GetAsync(t2.TemplateId).GetAwaiter().GetResult()!.TargetDisplayBinding.Kind); Assert.Equal(@"\\.\D2", s.GetAsync(t3.TemplateId).GetAwaiter().GetResult()!.TargetDisplayBinding.DeviceName); }
    [Fact] public void BothFlows_RoundTrip() { var s = CreateStore(); var id = Guid.NewGuid(); var t = new DesktopLayoutTemplate(id, "F", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { Z(Guid.NewGuid(), "C", R(0,0,5000,10000), 0, f: ZonePlacementFlow.ColumnMajor), Z(Guid.NewGuid(), "R", R(5000,0,10000,10000), 1, f: ZonePlacementFlow.RowMajor) }); s.SaveAsync(t).GetAwaiter().GetResult(); var l = s.GetAsync(id).GetAwaiter().GetResult(); Assert.Equal(ZonePlacementFlow.ColumnMajor, l!.Zones[0].PlacementFlow); Assert.Equal(ZonePlacementFlow.RowMajor, l.Zones[1].PlacementFlow); }
    [Fact] public void MaxItemNull_RoundTrip() { var s = CreateStore(); var id = Guid.NewGuid(); var t = MakeT(id: id, z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "U", R(0,0,10000,10000), 0) }); s.SaveAsync(t).GetAwaiter().GetResult(); Assert.Null(s.GetAsync(id).GetAwaiter().GetResult()!.Zones[0].MaxItemCount); }
    [Fact] public void MaxItemValue_RoundTrip() { var s = CreateStore(); var id = Guid.NewGuid(); var t = MakeT(id: id, z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "L", R(0,0,10000,10000), 0, max: 25) }); s.SaveAsync(t).GetAwaiter().GetResult(); Assert.Equal(25, s.GetAsync(id).GetAwaiter().GetResult()!.Zones[0].MaxItemCount); }
    [Fact] public void Name_SpacesPreserved() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id, name: "  S  ")).GetAwaiter().GetResult(); Assert.Equal("  S  ", s.GetAsync(id).GetAwaiter().GetResult()!.Name); }
    [Fact] public void ExactDeviceName_SpacesPreserved() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id, b: TargetDisplayBinding.ForExactDeviceName(@" \\.\D1 "))).GetAwaiter().GetResult(); Assert.Equal(@" \\.\D1 ", s.GetAsync(id).GetAwaiter().GetResult()!.TargetDisplayBinding.DeviceName); }
    [Fact] public void Version_RoundTrip() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id, ver: 7)).GetAwaiter().GetResult(); Assert.Equal(7, s.GetAsync(id).GetAwaiter().GetResult()!.Version); }
    [Fact] public void Zones_InputOrder() { var s = CreateStore(); var id = Guid.NewGuid(); var z = new[] { Z(Guid.NewGuid(), "C", R(8000,0,10000,5000), 2), Z(Guid.NewGuid(), "A", R(0,0,4000,5000), 5), Z(Guid.NewGuid(), "B", R(5000,0,7000,5000), 1) }; s.SaveAsync(new DesktopLayoutTemplate(id, "O", 1, TargetDisplayBinding.ForSelectedTarget(), z)).GetAwaiter().GetResult(); var l = s.GetAsync(id).GetAwaiter().GetResult(); Assert.Equal("C", l!.Zones[0].Name); Assert.Equal(2, l.Zones[0].SortOrder); Assert.Equal(5, l.Zones[1].SortOrder); }
    [Fact] public void Rect_CoordsRoundTrip() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id, z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "R", R(123,456,9876,9999), 0) })).GetAwaiter().GetResult(); var b = s.GetAsync(id).GetAwaiter().GetResult()!.Zones[0].Bounds; Assert.Equal(123, b.Left); Assert.Equal(456, b.Top); Assert.Equal(9876, b.Right); Assert.Equal(9999, b.Bottom); }
    [Fact] public void Get_NonExistent_Null() { Assert.Null(CreateStore().GetAsync(Guid.NewGuid()).GetAwaiter().GetResult()); }

    // ═══ Upsert ═══

    [Fact] public void Upsert_ReplacesParent() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id, name: "Old", ver: 1)).GetAwaiter().GetResult(); s.SaveAsync(MakeT(id: id, name: "New", ver: 3, b: TargetDisplayBinding.ForPrimaryDisplay())).GetAwaiter().GetResult(); var l = s.GetAsync(id).GetAwaiter().GetResult(); Assert.Equal("New", l!.Name); Assert.Equal(3, l.Version); }
    [Fact] public void Upsert_ReplacesZones() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id, z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "Old1", R(0,0,5000,10000), 0), Z(Guid.NewGuid(), "Old2", R(5000,0,10000,10000), 1) })).GetAwaiter().GetResult(); s.SaveAsync(MakeT(id: id, name: "R", z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "Only", R(0,0,10000,10000), 0) })).GetAwaiter().GetResult(); Assert.Single(s.GetAsync(id).GetAwaiter().GetResult()!.Zones); }

    // ═══ Transaction rollback ═══

    [Fact] public void MidSave_OldDataIntact()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id, z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "Orig", R(0,0,5000,10000), 0) })).GetAwaiter().GetResult();

        using (var c = OpenRawConnection())
        {
            using var tr = c.CreateCommand();
            tr.CommandText = @"CREATE TRIGGER IF NOT EXISTS t_abort BEFORE INSERT ON desktop_layout_template_zones FOR EACH ROW WHEN NEW.name='BOMB' BEGIN SELECT RAISE(ABORT,'boom'); END;";
            tr.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => s.SaveAsync(MakeT(id: id, name: "Bad", z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "G", R(0,0,5000,10000), 0), Z(Guid.NewGuid(), "BOMB", R(5000,0,10000,10000), 1) })).GetAwaiter().GetResult());

        var l = s.GetAsync(id).GetAwaiter().GetResult();
        Assert.Equal("Test", l!.Name);
        Assert.Single(l.Zones);
        Assert.Equal("Orig", l.Zones[0].Name);
    }

    // ═══ List ═══

    [Fact] public void List_Empty() { Assert.Empty(CreateStore().ListAsync().GetAwaiter().GetResult()); }
    [Fact] public void List_Order() { var s = CreateStore(); var tC = MakeT(id: Guid.Parse("00000000-0000-0000-0000-000000000003"), name: "Beta"); var tA = MakeT(id: Guid.Parse("00000000-0000-0000-0000-000000000001"), name: "Alpha"); var tB = MakeT(id: Guid.Parse("00000000-0000-0000-0000-000000000002"), name: "Alpha"); s.SaveAsync(tC).GetAwaiter().GetResult(); s.SaveAsync(tA).GetAwaiter().GetResult(); s.SaveAsync(tB).GetAwaiter().GetResult(); var r = s.ListAsync().GetAwaiter().GetResult(); Assert.Equal(tA.TemplateId, r[0].TemplateId); Assert.Equal(tB.TemplateId, r[1].TemplateId); Assert.Equal(tC.TemplateId, r[2].TemplateId); }
    [Fact] public void List_Limit() { var s = CreateStore(); for (var i = 0; i < 5; i++) s.SaveAsync(MakeT(id: Guid.NewGuid(), name: $"T{i}")).GetAwaiter().GetResult(); Assert.Equal(2, s.ListAsync(2).GetAwaiter().GetResult().Count); }
    [Fact] public void List_SummaryComplete() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(new DesktopLayoutTemplate(id, "Sum", 4, TargetDisplayBinding.ForExactDeviceName(@"\\.\D"), new[] { Z(Guid.NewGuid(), "A", R(0,0,5000,5000), 0), Z(Guid.NewGuid(), "B", R(5000,0,10000,5000), 1) })).GetAwaiter().GetResult(); var r = s.ListAsync().GetAwaiter().GetResult(); var sum = Assert.Single(r); Assert.Equal(id, sum.TemplateId); Assert.Equal("Sum", sum.Name); Assert.Equal(4, sum.Version); Assert.Equal(TargetDisplayBindingKind.ExactDeviceName, sum.TargetDisplayBinding.Kind); Assert.Equal(2, sum.ZoneCount); }

    // ═══ Delete ═══

    [Fact] public void Delete_Existing_True() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult(); Assert.True(s.DeleteAsync(id).GetAwaiter().GetResult()); Assert.Null(s.GetAsync(id).GetAwaiter().GetResult()); }
    [Fact] public void Delete_NonExistent_False() { Assert.False(CreateStore().DeleteAsync(Guid.NewGuid()).GetAwaiter().GetResult()); }
    [Fact] public void Delete_Cascades() { var s = CreateStore(); var id = Guid.NewGuid(); s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult(); s.DeleteAsync(id).GetAwaiter().GetResult(); using var c = OpenRawConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM desktop_layout_template_zones WHERE template_id=@id"; cmd.Parameters.AddWithValue("@id", id.ToString("D")); Assert.Equal(0L, (long)cmd.ExecuteScalar()!); }

    // ═══ Corruption: zone_count mismatch ═══

    [Fact]
    public void Corruption_ZoneCountMismatch_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id, z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "Z", R(0,0,5000,10000), 0) })).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE desktop_layout_templates SET zone_count=99 WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
        Assert.Contains("99", ex.Message);
    }

    // ═══ Corruption: invalid binding kind ═══

    [Fact]
    public void Corruption_InvalidBindingKind_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_templates SET target_display_binding_kind=99 WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void Corruption_InvalidBindingKind_ListRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_templates SET target_display_binding_kind=99 WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => s.ListAsync().GetAwaiter().GetResult());
    }

    // ═══ Corruption: binding combinations ═══

    [Fact]
    public void Corruption_SelectedTargetWithDeviceName_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id, b: TargetDisplayBinding.ForSelectedTarget())).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_templates SET target_display_device_name='EXTRA' WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
        Assert.Contains("EXTRA", ex.Message);
    }

    [Fact]
    public void Corruption_PrimaryDisplayWithDeviceName_ListRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id, b: TargetDisplayBinding.ForPrimaryDisplay())).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_templates SET target_display_device_name='X' WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.ListAsync().GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
    }

    [Fact]
    public void Corruption_ExactDeviceNameNull_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id, b: TargetDisplayBinding.ForExactDeviceName(@"\\.\D1"))).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_templates SET target_display_device_name=NULL WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
        Assert.Contains("null", ex.Message.ToLowerInvariant());
    }

    // ═══ Corruption: placement flow ═══

    [Fact]
    public void Corruption_InvalidPlacementFlow_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_template_zones SET placement_flow=99 WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
        Assert.Contains("PlacementFlow", ex.Message);
    }

    // ═══ Corruption: invalid ZoneId ═══

    [Fact]
    public void Corruption_InvalidZoneId_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE desktop_layout_template_zones SET zone_id='NOT-A-GUID' WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
        Assert.Contains("NOT-A-GUID", ex.Message);
    }

    // ═══ Corruption: invalid TemplateId in list ═══

    [Fact]
    public void Corruption_InvalidTemplateId_ListRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var fk = c.CreateCommand();
            fk.CommandText = "PRAGMA foreign_keys = OFF;";
            fk.ExecuteNonQuery();
            using var up = c.CreateCommand();
            up.CommandText = "UPDATE desktop_layout_templates SET template_id='BAD' WHERE template_id=@id";
            up.Parameters.AddWithValue("@id", id.ToString("D"));
            up.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.ListAsync().GetAwaiter().GetResult());
        Assert.Contains("BAD", ex.Message);
    }

    // ═══ Corruption: illegal coordinates ═══

    [Fact]
    public void Corruption_IllegalCoordinates_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id)).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_template_zones SET right_value=99999 WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
    }

    // ═══ Corruption: illegal MaxItemCount ═══

    [Fact]
    public void Corruption_IllegalMaxItemCount_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id, z: new List<DesktopLayoutZone> { Z(Guid.NewGuid(), "Z", R(0,0,5000,10000), 0, max: 5) })).GetAwaiter().GetResult();
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_template_zones SET max_item_count=0 WHERE template_id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
    }

    // ═══ Corruption: domain invariant (overlapping zone Bounds) ═══

    [Fact]
    public void Corruption_OverlappingZones_GetRejects()
    {
        var s = CreateStore(); var id = Guid.NewGuid();
        s.SaveAsync(MakeT(id: id, z: new List<DesktopLayoutZone>
        {
            Z(Guid.NewGuid(), "A", R(0, 0, 5000, 10000), 0),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1),
        })).GetAwaiter().GetResult();

        // Corrupt B's bounds to overlap with A
        using (var c = OpenRawConnection())
        {
            using var up = c.CreateCommand();
            up.CommandText = @"PRAGMA ignore_check_constraints = ON; UPDATE desktop_layout_template_zones SET left_value=0 WHERE template_id=@id AND sort_order=1;";
            up.Parameters.AddWithValue("@id", id.ToString("D"));
            up.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidDataException>(() => s.GetAsync(id).GetAwaiter().GetResult());
        Assert.Contains(id.ToString("D"), ex.Message);
    }

    // ═══ Parameter validation ═══

    [Fact] public void Get_EmptyGuid() { Assert.Throws<ArgumentException>(() => CreateStore().GetAsync(Guid.Empty).GetAwaiter().GetResult()); }
    [Fact] public void Delete_EmptyGuid() { Assert.Throws<ArgumentException>(() => CreateStore().DeleteAsync(Guid.Empty).GetAwaiter().GetResult()); }
    [Fact] public void Save_Null() { Assert.Throws<ArgumentNullException>(() => CreateStore().SaveAsync(null!).GetAwaiter().GetResult()); }
    [Fact] public void List_LimitZero() { Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore().ListAsync(0).GetAwaiter().GetResult()); }
    [Fact] public void List_NegLimit() { Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore().ListAsync(-1).GetAwaiter().GetResult()); }

    // ═══ Cancellation ═══

    [Fact] public void Cancel_Init() { using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => new SqliteDesktopLayoutTemplateStore(_dbPath).InitializeAsync(cts.Token).GetAwaiter().GetResult()); }
    [Fact] public void Cancel_Save() { using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => CreateStore().SaveAsync(MakeT(), cts.Token).GetAwaiter().GetResult()); }
    [Fact] public void Cancel_Get() { using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => CreateStore().GetAsync(Guid.NewGuid(), cts.Token).GetAwaiter().GetResult()); }
    [Fact] public void Cancel_List() { using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => CreateStore().ListAsync(cancellationToken: cts.Token).GetAwaiter().GetResult()); }
    [Fact] public void Cancel_Delete() { using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => CreateStore().DeleteAsync(Guid.NewGuid(), cts.Token).GetAwaiter().GetResult()); }
}
