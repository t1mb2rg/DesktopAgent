using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Poc.LayoutTemplates.Editor;

namespace DesktopAgent.Tests.Poc.LayoutTemplates.Editor;

public sealed class LayoutTemplateEditorDraftTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so, int? max = null) =>
        new(id, n, b, so, ZonePlacementFlow.ColumnMajor, max);

    [Fact] public void CreateNew_HasNonEmptyTemplateId() { var d = LayoutTemplateEditorDraft.CreateNew(); Assert.NotEqual(Guid.Empty, d.TemplateId); }
    [Fact] public void CreateNew_DefaultFields() { var d = LayoutTemplateEditorDraft.CreateNew(); Assert.Equal("新布局模板", d.Name); Assert.Equal(1, d.Version); Assert.Equal(TargetDisplayBindingKind.SelectedTarget, d.BindingKind); }
    [Fact] public void CreateNew_HasOneFullScreenZone() { var d = LayoutTemplateEditorDraft.CreateNew(); Assert.Single(d.Zones); Assert.Equal(0, d.Zones[0].Left); Assert.Equal(0, d.Zones[0].Top); Assert.Equal(10000, d.Zones[0].Right); Assert.Equal(10000, d.Zones[0].Bottom); }
    [Fact] public void CreateNew_IsDirty_NotPersisted() { var d = LayoutTemplateEditorDraft.CreateNew(); Assert.True(d.IsDirty); Assert.False(d.IsPersisted); }

    [Fact] public void FromTemplate_PreservesTemplateId()
    {
        var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "Test", 2,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 5000, 5000), 0) });
        var d = LayoutTemplateEditorDraft.FromTemplate(tmpl);
        Assert.Equal(tmpl.TemplateId, d.TemplateId);
    }

    [Fact] public void FromTemplate_PreservesZoneId()
    {
        var zid = Guid.NewGuid();
        var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "Test", 1,
            TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(zid, "Z", R(0, 0, 5000, 5000), 0) });
        var d = LayoutTemplateEditorDraft.FromTemplate(tmpl);
        Assert.Equal(zid, d.Zones[0].ZoneId);
    }

    [Fact] public void FromTemplate_PreservesInputOrder_NotSortOrder()
    {
        var z0 = Z(Guid.NewGuid(), "Z0", R(0, 0, 3000, 10000), 5);
        var z1 = Z(Guid.NewGuid(), "Z1", R(4000, 0, 10000, 10000), 1);
        var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForSelectedTarget(), new[] { z0, z1 });
        var d = LayoutTemplateEditorDraft.FromTemplate(tmpl);
        Assert.Equal("Z0", d.Zones[0].Name);
        Assert.Equal(5, d.Zones[0].SortOrder);
        Assert.Equal("Z1", d.Zones[1].Name);
        Assert.Equal(1, d.Zones[1].SortOrder);
    }

    [Fact] public void FromTemplate_PreservesExactDeviceName_Verbatim()
    {
        var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForExactDeviceName("  DISPLAY1  "),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 5000, 5000), 0) });
        var d = LayoutTemplateEditorDraft.FromTemplate(tmpl);
        Assert.Equal("  DISPLAY1  ", d.ExactDeviceName);
    }

    [Fact] public void FromTemplate_InitiallyClean_Persisted() { var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 5000, 5000), 0) }); var d = LayoutTemplateEditorDraft.FromTemplate(tmpl); Assert.False(d.IsDirty); Assert.True(d.IsPersisted); }

    [Fact] public void EditName_MakesDirty() { var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate()); Assert.False(d.IsDirty); d.Name = "Changed"; Assert.True(d.IsDirty); }
    [Fact] public void EditZone_MakesDirty() { var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate()); Assert.False(d.IsDirty); d.Zones[0].Name = "Changed"; Assert.True(d.IsDirty); }
    [Fact] public void AddZone_MakesDirty() { var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate(2)); Assert.False(d.IsDirty); d.Zones.Add(MakeZoneDraft("New", 2000, 2000, 3000, 3000, 2)); Assert.True(d.IsDirty); }
    [Fact] public void RemoveZone_MakesDirty() { var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate(2)); d.MarkSaved(); Assert.False(d.IsDirty); d.Zones.RemoveAt(1); Assert.True(d.IsDirty); }

    [Fact] public void RemoveZone_UnsubscribesEvents()
    {
        var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate(2));
        var removed = d.Zones[1];
        d.Zones.RemoveAt(1);
        d.MarkSaved();
        Assert.False(d.IsDirty);
        removed.Name = "ShouldNotAffect"; // 已取消订阅，不应触发 dirty
        Assert.False(d.IsDirty);
    }

    [Fact] public void MarkSaved_ClearsDirty_SetsPersisted() { var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate()); d.Name = "Changed"; Assert.True(d.IsDirty); d.MarkSaved(); Assert.False(d.IsDirty); Assert.True(d.IsPersisted); }

    [Fact] public void TryBuild_SelectedTarget_Success()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.BindingKind = TargetDisplayBindingKind.SelectedTarget;
        var r = d.TryBuild();
        Assert.True(r.IsValid); Assert.NotNull(r.Template);
    }

    [Fact] public void TryBuild_PrimaryDisplay_Success()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.BindingKind = TargetDisplayBindingKind.PrimaryDisplay;
        var r = d.TryBuild();
        Assert.True(r.IsValid);
    }

    [Fact] public void TryBuild_ExactDeviceName_Success()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.BindingKind = TargetDisplayBindingKind.ExactDeviceName;
        d.ExactDeviceName = "DISPLAY1";
        var r = d.TryBuild();
        Assert.True(r.IsValid);
    }

    [Fact] public void TryBuild_ExactDeviceName_Blank_Fails()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.BindingKind = TargetDisplayBindingKind.ExactDeviceName;
        d.ExactDeviceName = "";
        var r = d.TryBuild();
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("ExactDeviceName"));
    }

    [Fact] public void TryBuild_NameBlank_Fails() { var d = LayoutTemplateEditorDraft.CreateNew(); d.Name = "  "; var r = d.TryBuild(); Assert.False(r.IsValid); }
    [Fact] public void TryBuild_VersionZero_Fails() { var d = LayoutTemplateEditorDraft.CreateNew(); d.Version = 0; var r = d.TryBuild(); Assert.False(r.IsValid); }
    [Fact] public void TryBuild_NoZones_Fails() { var d = LayoutTemplateEditorDraft.CreateNew(); d.Zones.Clear(); var r = d.TryBuild(); Assert.False(r.IsValid); }

    [Fact] public void TryBuild_InvalidBounds_Fails()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.Zones[0].Right = 5000;
        d.Zones[0].Left = 6000; // Left > Right
        var r = d.TryBuild();
        Assert.False(r.IsValid);
    }

    [Fact] public void TryBuild_OverlappingZones_Fails()
    {
        var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate(1));
        d.Zones.Add(MakeZoneDraft("Z2", 0, 0, 5000, 5000, 1)); // 与第一个 zone 完全重叠
        var r = d.TryBuild();
        Assert.False(r.IsValid);
    }

    [Fact] public void TryBuild_DuplicateZoneId_Fails()
    {
        var zid = Guid.NewGuid();
        var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(zid, "Z1", R(0, 0, 4000, 10000), 0) });
        var d = LayoutTemplateEditorDraft.FromTemplate(tmpl);
        d.Zones.Add(new LayoutZoneEditorDraft(zid, "Z2", 5000, 0, 10000, 10000, 1, ZonePlacementFlow.ColumnMajor, null));
        var r = d.TryBuild();
        Assert.False(r.IsValid);
    }

    [Fact] public void TryBuild_DuplicateSortOrder_Fails()
    {
        var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate(1));
        d.Zones.Add(MakeZoneDraft("Z2", 5000, 0, 10000, 10000, 0)); // 相同 SortOrder=0
        var r = d.TryBuild();
        Assert.False(r.IsValid);
    }

    [Fact] public void TryBuild_IllegalPlacementFlow_Fails()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.Zones[0].PlacementFlow = (ZonePlacementFlow)999;
        var r = d.TryBuild();
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("PlacementFlow"));
    }

    [Fact] public void TryBuild_RoundTrip_PreservesSemantics()
    {
        var tid = Guid.NewGuid();
        var z1Id = Guid.NewGuid();
        var z2Id = Guid.NewGuid();
        var tmpl = new DesktopLayoutTemplate(tid, "RoundTrip", 3,
            TargetDisplayBinding.ForExactDeviceName(" Test "),
            new[] {
                new DesktopLayoutZone(z1Id, "Zone A", R(500, 1000, 3500, 8000), 5, ZonePlacementFlow.ColumnMajor, maxItemCount: 20),
                new DesktopLayoutZone(z2Id, "Zone B", R(6000, 2000, 9500, 9000), 2, ZonePlacementFlow.RowMajor, maxItemCount: null)
            });
        var d = LayoutTemplateEditorDraft.FromTemplate(tmpl);
        var r = d.TryBuild();
        Assert.True(r.IsValid);
        var rebuilt = r.Template!;

        Assert.Equal(tid, rebuilt.TemplateId);
        Assert.Equal("RoundTrip", rebuilt.Name);
        Assert.Equal(3, rebuilt.Version);
        Assert.Equal(TargetDisplayBindingKind.ExactDeviceName, rebuilt.TargetDisplayBinding.Kind);
        Assert.Equal(" Test ", rebuilt.TargetDisplayBinding.DeviceName);
        Assert.Equal(2, rebuilt.Zones.Count);

        // Zone 0
        Assert.Equal(z1Id, rebuilt.Zones[0].ZoneId);
        Assert.Equal("Zone A", rebuilt.Zones[0].Name);
        Assert.Equal(500, rebuilt.Zones[0].Bounds.Left);
        Assert.Equal(1000, rebuilt.Zones[0].Bounds.Top);
        Assert.Equal(3500, rebuilt.Zones[0].Bounds.Right);
        Assert.Equal(8000, rebuilt.Zones[0].Bounds.Bottom);
        Assert.Equal(5, rebuilt.Zones[0].SortOrder);
        Assert.Equal(ZonePlacementFlow.ColumnMajor, rebuilt.Zones[0].PlacementFlow);
        Assert.Equal(20, rebuilt.Zones[0].MaxItemCount);

        // Zone 1
        Assert.Equal(z2Id, rebuilt.Zones[1].ZoneId);
        Assert.Equal("Zone B", rebuilt.Zones[1].Name);
        Assert.Equal(6000, rebuilt.Zones[1].Bounds.Left);
        Assert.Equal(2000, rebuilt.Zones[1].Bounds.Top);
        Assert.Equal(9500, rebuilt.Zones[1].Bounds.Right);
        Assert.Equal(9000, rebuilt.Zones[1].Bounds.Bottom);
        Assert.Equal(2, rebuilt.Zones[1].SortOrder);
        Assert.Equal(ZonePlacementFlow.RowMajor, rebuilt.Zones[1].PlacementFlow);
        Assert.Null(rebuilt.Zones[1].MaxItemCount);
    }

    [Fact] public void TryBuild_Errors_ReadOnly()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.Zones.Clear();
        var r = d.TryBuild();
        Assert.False(r.IsValid);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)r.Errors).Clear());
    }

    [Fact] public void FromTemplate_DoesNotTrim_ZoneName() { var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { Z(Guid.NewGuid(), " z ", R(0, 0, 5000, 5000), 0) }); var d = LayoutTemplateEditorDraft.FromTemplate(tmpl); Assert.Equal(" z ", d.Zones[0].Name); }

    [Fact] public void MutateOriginalTemplate_Zones_NoEffectOnDraft()
    {
        var tmpl = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 5000, 5000), 0) });
        var d = LayoutTemplateEditorDraft.FromTemplate(tmpl);
        d.Zones[0].Name = "Changed Local";
        Assert.Equal("Z", tmpl.Zones[0].Name);
    }

    // ═══ boundary / subscription ═══

    [Fact] public void TryBuild_ExactDeviceName_Whitespace_Fails()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.BindingKind = TargetDisplayBindingKind.ExactDeviceName;
        d.ExactDeviceName = "   ";
        var r = d.TryBuild();
        Assert.False(r.IsValid);
    }

    [Fact] public void TryBuild_ExactDeviceName_NotTrimmed()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.BindingKind = TargetDisplayBindingKind.ExactDeviceName;
        d.ExactDeviceName = " Dev ";
        var r = d.TryBuild();
        Assert.True(r.IsValid);
        Assert.Equal(" Dev ", r.Template!.TargetDisplayBinding.DeviceName);
    }

    [Fact] public void TryBuild_MaxItemCount_Zero_Fails()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.Zones[0].MaxItemCount = 0;
        var r = d.TryBuild();
        Assert.False(r.IsValid);
    }

    [Fact] public void TryBuild_MaxItemCount_Negative_Fails()
    {
        var d = LayoutTemplateEditorDraft.CreateNew();
        d.Zones[0].MaxItemCount = -1;
        var r = d.TryBuild();
        Assert.False(r.IsValid);
    }

    [Fact] public void ClearZones_OldZones_NoLongerTriggerDirty()
    {
        var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate(2));
        d.MarkSaved();
        Assert.False(d.IsDirty);
        var removed = d.Zones[1];
        d.Zones.Clear();
        d.MarkSaved();
        Assert.False(d.IsDirty);
        removed.Name = "StillTriggered?"; // 已取消订阅
        Assert.False(d.IsDirty);
    }

    [Fact] public void ReplaceZone_OldZone_NoLongerDirty_NewZone_CanDirty()
    {
        var d = LayoutTemplateEditorDraft.FromTemplate(MakeTemplate(2));
        d.MarkSaved();
        var oldZone = d.Zones[1];
        var newZone = new LayoutZoneEditorDraft(
            Guid.NewGuid(), "Replaced", 6000, 0, 8000, 5000, 1,
            ZonePlacementFlow.ColumnMajor, null);
        d.Zones[1] = newZone;
        d.MarkSaved();
        Assert.False(d.IsDirty);
        oldZone.Name = "OldChanged"; // 已取消订阅
        Assert.False(d.IsDirty);
        newZone.Name = "NewChanged"; // 已订阅
        Assert.True(d.IsDirty);
    }

    // ═══ helpers ═══

    private static DesktopLayoutTemplate MakeTemplate(int zoneCount = 1)
    {
        var zones = new List<DesktopLayoutZone>();
        for (var i = 0; i < zoneCount; i++)
            zones.Add(Z(Guid.NewGuid(), $"Z{i}", R(i * 2000, 0, (i + 1) * 2000, 5000), i));
        return new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForSelectedTarget(), zones);
    }

    private static LayoutZoneEditorDraft MakeZoneDraft(string name, int l, int t, int r, int b, int so) =>
        new(Guid.NewGuid(), name, l, t, r, b, so, ZonePlacementFlow.ColumnMajor, null);
}
