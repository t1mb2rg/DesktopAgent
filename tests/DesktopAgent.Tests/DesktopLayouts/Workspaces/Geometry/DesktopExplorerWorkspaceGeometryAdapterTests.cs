using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class DesktopExplorerWorkspaceGeometryAdapterTests
{
    [Fact]
    public void Build_StrongPhaseWithNegativeDisplay_ProducesNativeSlotsWithoutCurrentCellProjection()
    {
        var displays = new[]
        {
            Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true),
            Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
        };
        var observation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                // 合法 Windows 事实，但位于 native slot surface 尾部，不要求先投影成 CurrentCell。
                Icon("tail", "Tail", 799, 759, "DISPLAY1"),
                Icon("l1", "L1", -770, 50, "DISPLAY2"),
                Icon("l2", "L2", -690, 130, "DISPLAY2"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.True(result.Succeeded);
        var geometry = Assert.IsType<ExplorerWorkspaceGeometry>(result.Geometry);
        Assert.Equal(2, geometry.Displays.Count);
        Assert.Equal("primary", geometry.Displays.Single(display => display.IsPrimary).DisplayRef);

        var left = geometry.Displays.Single(display => display.DeviceName == "DISPLAY2");
        Assert.Equal("secondary_1", left.DisplayRef);
        Assert.True(left.GridOrigin.X < 0);
        Assert.Equal(30, PositiveModulo(left.GridOrigin.X, geometry.Spacing.X));
        Assert.Equal(50, PositiveModulo(left.GridOrigin.Y, geometry.Spacing.Y));

        Assert.All(geometry.Displays.SelectMany(static display => display.Slots), slot =>
        {
            Assert.Equal(30, PositiveModulo(slot.PixelPosition.X, geometry.Spacing.X));
            Assert.Equal(50, PositiveModulo(slot.PixelPosition.Y, geometry.Spacing.Y));
        });

        Assert.DoesNotContain(
            typeof(ExplorerWorkspaceGeometry).GetProperties(),
            property => property.Name.Contains("CurrentCell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(ExplorerFixedObstacle).GetProperties(),
            property => property.Name.Contains("CurrentCell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_SystemItemKind_IsFixedObstacleEvenWhenLegacyFlagIsFalse()
    {
        var observation = Observation(
            [Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true)],
            [
                Icon(
                    "recycle",
                    "Recycle Bin",
                    30,
                    50,
                    "DISPLAY1",
                    itemKind: ItemKind.SystemItem,
                    isSystemItem: false),
                Icon("a", "A", 110, 130, "DISPLAY1"),
                Icon("b", "B", 190, 210, "DISPLAY1"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.True(result.Succeeded);
        var display = Assert.Single(result.Geometry!.Displays);
        var obstacle = Assert.Single(display.FixedObstacles);
        Assert.Equal("recycle", obstacle.StableId);
        Assert.Contains("primary:slot:0000:0000", obstacle.BlockedSlotIds);
        Assert.DoesNotContain(display.FixedObstacles, item => item.StableId == "a" || item.StableId == "b");
    }

    [Fact]
    public void Build_ScatteredFreePlacement_FailsGeometryWithoutInventingPhase()
    {
        var observation = Observation(
            [Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true)],
            [
                Icon("a", "A", 3, 5, "DISPLAY1"),
                Icon("b", "B", 22, 17, "DISPLAY1"),
                Icon("c", "C", 41, 39, "DISPLAY1"),
                Icon("d", "D", 63, 71, "DISPLAY1"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.False(result.Succeeded);
        Assert.Null(result.Geometry);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseUnavailable);
    }

    [Fact]
    public void Build_ReliableButConflictingDisplayPhases_FailsClosed()
    {
        var observation = Observation(
            [
                Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true),
                Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
            ],
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("l1", "L1", -790, 20, "DISPLAY2"),
                Icon("l2", "L2", -710, 100, "DISPLAY2"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseConflict);
    }

    [Fact]
    public void Build_IconsOutsideEveryDisplay_AreReportedAsNoticesInsteadOfFailingTheMachine()
    {
        // 真机实测（2026-10-14）：显示器排布非矩形时，桌面列表视图仍横跨整个虚拟桌面并集，
        // 本机 18 个既有图标停在没有任何显示器覆盖的缝隙里（超出 DISPLAY1 底边、落在 DISPLAY3 右侧…）。
        // 这些项目不在任何屏的 WorkArea 内，不可能影响任何一屏的 slot surface，
        // 因此必须是"本轮不可规划"的通知，而不是让整台机器 fail closed。
        var observation = Observation(
            [Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true)],
            [
                Icon("a", "A", 30, 50, "DISPLAY1"),
                Icon("b", "B", 110, 130, "DISPLAY1"),
                Icon("c", "C", 190, 210, "DISPLAY1"),
                Icon("d", "D", 270, 290, "DISPLAY1"),
                Icon("ghost", "Ghost", -200, 900, "DISPLAY1"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.True(result.Succeeded, "落在所有显示器之外的图标不应阻塞几何构建。");
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Geometry);
        Assert.Contains(
            result.Notices,
            notice => notice.Code == DesktopExplorerWorkspaceGeometryIssueCode.IconOutsideAllDisplays
                && notice.ItemDisplayName == "Ghost");
    }

    [Fact]
    public void Build_IconInsideAnotherDisplayThanItsAssignedOne_StillFailsClosed()
    {
        // 与"落在所有显示器之外"不同：图标在别的显示器范围之内，说明 Shell 归属与物理落点真的矛盾，
        // 它会影响 slot surface 的障碍物归属，必须继续 fail closed。
        var observation = Observation(
            [
                Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true),
                Display("DISPLAY2", 800, 0, 1600, 800, 800, 0, 1600, 800, isPrimary: false),
            ],
            [
                Icon("a", "A", 30, 50, "DISPLAY2"),
                Icon("b", "B", 110, 130, "DISPLAY1"),
                Icon("c", "C", 190, 210, "DISPLAY1"),
                Icon("d", "D", 270, 290, "DISPLAY1"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.IconOutsideAssignedDisplay);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public void GeometryFingerprint_IgnoresMovableSourcePositionButTracksFixedObstacleSurface()
    {
        var display = Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true);
        var first = Observation(
            [display],
            [
                Icon("system", "System", 30, 50, "DISPLAY1", ItemKind.SystemItem, isSystemItem: false),
                Icon("a", "A", 110, 130, "DISPLAY1"),
                Icon("b", "B", 190, 210, "DISPLAY1"),
            ]);
        var second = Observation(
            [display],
            [
                Icon("system", "System", 30, 50, "DISPLAY1", ItemKind.SystemItem, isSystemItem: false),
                Icon("a", "A", 270, 290, "DISPLAY1"),
                Icon("b", "B", 350, 370, "DISPLAY1"),
            ]);
        var fixedMoved = Observation(
            [display],
            [
                Icon("system", "System", 110, 130, "DISPLAY1", ItemKind.SystemItem, isSystemItem: false),
                Icon("a", "A", 270, 290, "DISPLAY1"),
                Icon("b", "B", 350, 370, "DISPLAY1"),
            ]);

        var firstGeometry = DesktopExplorerWorkspaceGeometryAdapter.Build(first).Geometry!;
        var secondGeometry = DesktopExplorerWorkspaceGeometryAdapter.Build(second).Geometry!;
        var fixedMovedGeometry = DesktopExplorerWorkspaceGeometryAdapter.Build(fixedMoved).Geometry!;

        Assert.Equal(firstGeometry.GeometryFingerprint, secondGeometry.GeometryFingerprint);
        Assert.NotEqual(firstGeometry.GeometryFingerprint, fixedMovedGeometry.GeometryFingerprint);
    }

    [Fact]
    public void Build_WithoutFacts_ReportsEveryDisplayAsInferredProvenance()
    {
        var displays = new[]
        {
            Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true),
            Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
        };
        var observation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("p3", "P3", 190, 210, "DISPLAY1"),
                Icon("l1", "L1", -770, 50, "DISPLAY2"),
                Icon("l2", "L2", -690, 130, "DISPLAY2"),
                Icon("l3", "L3", -610, 210, "DISPLAY2"),
            ],
            TestSpacing);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.True(result.Succeeded, "不传事实时行为不变：仍走图标残差推断并成功建立几何。");
        Assert.Equal(
            ExplorerGridTopologyIdentity.Compute(false, TestSpacing, displays),
            result.TopologyIdentity);
        Assert.Equal(2, result.Provenance.Count);
        Assert.All(result.Provenance, entry =>
        {
            Assert.Equal(ExplorerGridEvidenceSource.Inferred, entry.Source);
            Assert.Equal(TestSpacing, entry.Spacing);
            Assert.Equal(new PixelPoint(30, 50), entry.Phase);
            Assert.Equal(3, entry.SampleCount);
            Assert.Equal(3, entry.ConsensusCount);
        });
        Assert.Empty(result.DemotedFacts);
    }

    [Fact]
    public void Build_MeasuredFactWinsOverIconResidueInference_ForItsOwnDisplay()
    {
        var display = Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true);
        var identity = ExplorerGridTopologyIdentity.Compute(false, TestSpacing, [display]);
        // 图标残差会推出 (30,50)；实测事实给出不同的 (10,20)，实测优先。
        var observation = Observation(
            [display],
            [
                Icon("a", "A", 30, 50, "DISPLAY1"),
                Icon("b", "B", 110, 130, "DISPLAY1"),
                Icon("c", "C", 190, 210, "DISPLAY1"),
            ],
            TestSpacing);
        var fact = Measurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(10, 20),
            identity,
            sampleCount: 4,
            consensusCount: 4,
            spacingConstant: true,
            phaseConstant: true);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation, [fact]);

        Assert.True(result.Succeeded, "绑定当前拓扑的实测事实应当优先于图标残差推断。");
        var geometryDisplay = Assert.Single(result.Geometry!.Displays);
        Assert.Equal(new PixelPoint(10, 20), geometryDisplay.Phase);

        var entry = Assert.Single(result.Provenance);
        Assert.Equal("DISPLAY1", entry.DeviceName);
        Assert.Equal(ExplorerGridEvidenceSource.Measured, entry.Source);
        Assert.Equal(TestSpacing, entry.Spacing);
        Assert.Equal(new PixelPoint(10, 20), entry.Phase);
        Assert.Equal(4, entry.SampleCount);
        Assert.Equal(4, entry.ConsensusCount);

        Assert.Empty(result.DemotedFacts);
        Assert.Equal(identity, result.TopologyIdentity);

        // 施工面必须真的按实测 phase 对齐，而不只是 Provenance 上写着 Measured。
        Assert.All(geometryDisplay.Slots, slot =>
        {
            Assert.Equal(10, PositiveModulo(slot.PixelPosition.X, TestSpacing.X));
            Assert.Equal(20, PositiveModulo(slot.PixelPosition.Y, TestSpacing.Y));
        });
    }

    [Fact]
    public void Build_StaleTopologyFact_IsDemotedAndFallsBackToItsOwnInference()
    {
        var display = Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true);
        var currentIdentity = ExplorerGridTopologyIdentity.Compute(false, TestSpacing, [display]);
        // 任务栏变化后的拓扑身份：事实产生时的环境已经不再成立。
        var staleIdentity = ExplorerGridTopologyIdentity.Compute(
            false,
            TestSpacing,
            [Display("DISPLAY1", 0, 0, 800, 800, 0, 48, 800, 800, isPrimary: true)]);
        var staleFact = Measurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(10, 20),
            staleIdentity,
            sampleCount: 4,
            consensusCount: 4,
            spacingConstant: true,
            phaseConstant: true);

        var reliableObservation = Observation(
            [display],
            [
                Icon("a", "A", 30, 50, "DISPLAY1"),
                Icon("b", "B", 110, 130, "DISPLAY1"),
                Icon("c", "C", 190, 210, "DISPLAY1"),
            ],
            TestSpacing);

        var fallback = DesktopExplorerWorkspaceGeometryAdapter.Build(reliableObservation, [staleFact]);

        Assert.True(fallback.Succeeded, "陈旧的实测事实只能被显式降级，不能继续授权 phase。");
        Assert.Equal(currentIdentity, fallback.TopologyIdentity);

        var demotion = Assert.Single(fallback.DemotedFacts);
        Assert.Equal("DISPLAY1", demotion.DeviceName);
        Assert.Contains("拓扑身份", demotion.Reason);
        Assert.Equal(staleIdentity, demotion.FactTopologyFingerprint);
        Assert.Equal(currentIdentity, demotion.CurrentTopologyIdentity);

        var fallbackEntry = Assert.Single(fallback.Provenance);
        Assert.Equal(ExplorerGridEvidenceSource.Inferred, fallbackEntry.Source);
        Assert.Equal(new PixelPoint(30, 50), fallbackEntry.Phase);
        Assert.Equal(new PixelPoint(30, 50), fallback.Geometry!.Displays[0].Phase);

        // 降级之后推断本身也不足：必须 fail-closed，而不是借用陈旧事实。
        var weakObservation = Observation(
            [display],
            [
                Icon("d", "D", 3, 5, "DISPLAY1"),
                Icon("e", "E", 22, 17, "DISPLAY1"),
                Icon("f", "F", 41, 39, "DISPLAY1"),
                Icon("g", "G", 63, 71, "DISPLAY1"),
            ],
            TestSpacing);

        var failed = DesktopExplorerWorkspaceGeometryAdapter.Build(weakObservation, [staleFact]);

        Assert.False(failed.Succeeded);
        Assert.Null(failed.Geometry);
        Assert.Contains(
            failed.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseUnavailable);
        Assert.DoesNotContain(
            failed.Provenance,
            entry => entry.Source == ExplorerGridEvidenceSource.Measured);
        Assert.Contains(
            failed.DemotedFacts,
            item => item.Reason.Contains("拓扑身份", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_InferredSourceFact_IsDemotedAndNeverUsedAsMeasured()
    {
        var display = Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true);
        var identity = ExplorerGridTopologyIdentity.Compute(false, TestSpacing, [display]);
        var observation = Observation(
            [display],
            [
                Icon("a", "A", 30, 50, "DISPLAY1"),
                Icon("b", "B", 110, 130, "DISPLAY1"),
                Icon("c", "C", 190, 210, "DISPLAY1"),
            ],
            TestSpacing);

        // 事实本身合法，但来源是多数投票：即便它绑定当前拓扑也不能授权执行。
        var fact = Measurement(
            ExplorerGridEvidenceSource.Inferred,
            "DISPLAY1",
            new PixelPoint(10, 20),
            identity,
            sampleCount: 4,
            consensusCount: 3,
            spacingConstant: false,
            phaseConstant: false);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation, [fact]);

        Assert.True(result.Succeeded, "非实测来源被降级后，仍应由该显示器自己的推断建立施工面。");

        var demotion = Assert.Single(result.DemotedFacts);
        Assert.Equal("DISPLAY1", demotion.DeviceName);
        Assert.Contains("非实测来源", demotion.Reason);
        Assert.Equal(identity, demotion.FactTopologyFingerprint);
        Assert.Equal(identity, demotion.CurrentTopologyIdentity);

        var entry = Assert.Single(result.Provenance);
        Assert.Equal(ExplorerGridEvidenceSource.Inferred, entry.Source);
        Assert.Equal(new PixelPoint(30, 50), entry.Phase);
        Assert.Equal(3, entry.SampleCount);
        Assert.Equal(3, entry.ConsensusCount);
        Assert.Equal(new PixelPoint(30, 50), result.Geometry!.Displays[0].Phase);
    }

    [Fact]
    public void Build_EveryDisplayHasMeasuredFact_AllowsPerDisplayPhase()
    {
        var displays = new[]
        {
            Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true),
            Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
        };
        var identity = ExplorerGridTopologyIdentity.Compute(false, TestSpacing, displays);
        var observation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("l1", "L1", -770, 50, "DISPLAY2"),
                Icon("l2", "L2", -690, 130, "DISPLAY2"),
            ],
            TestSpacing);

        var firstFact = Measurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(10, 20),
            identity,
            sampleCount: 3,
            consensusCount: 3,
            spacingConstant: true,
            phaseConstant: true);
        var secondFact = Measurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY2",
            new PixelPoint(60, 70),
            identity,
            sampleCount: 4,
            consensusCount: 4,
            spacingConstant: true,
            phaseConstant: true);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation, [firstFact, secondFact]);

        Assert.True(result.Succeeded, "每块显示器都有绑定当前拓扑的实测事实时，不应再要求共享 phase。");
        var first = result.Geometry!.Displays.Single(entry => entry.DeviceName == "DISPLAY1");
        var second = result.Geometry!.Displays.Single(entry => entry.DeviceName == "DISPLAY2");
        Assert.Equal(new PixelPoint(10, 20), first.Phase);
        Assert.Equal(new PixelPoint(60, 70), second.Phase);

        Assert.Equal(2, result.Provenance.Count);
        Assert.All(result.Provenance, entry =>
        {
            Assert.Equal(ExplorerGridEvidenceSource.Measured, entry.Source);
            Assert.Equal(
                entry.DeviceName == "DISPLAY1" ? new PixelPoint(10, 20) : new PixelPoint(60, 70),
                entry.Phase);
        });
        Assert.Empty(result.DemotedFacts);
    }

    [Fact]
    public void Build_MixedProvenance_RequiresConsensusForTheInferredDisplay()
    {
        var displays = new[]
        {
            Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true),
            Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
        };
        var identity = ExplorerGridTopologyIdentity.Compute(false, TestSpacing, displays);
        var fact = Measurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(10, 20),
            identity,
            sampleCount: 3,
            consensusCount: 3,
            spacingConstant: true,
            phaseConstant: true);

        var reliableObservation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("l1", "L1", -770, 50, "DISPLAY2"),
                Icon("l2", "L2", -690, 130, "DISPLAY2"),
                Icon("l3", "L3", -610, 210, "DISPLAY2"),
            ],
            TestSpacing);

        var succeeded = DesktopExplorerWorkspaceGeometryAdapter.Build(reliableObservation, [fact]);

        Assert.True(succeeded.Succeeded, "实测显示器用自己的事实，推断显示器必须自己达到共识门槛。");
        var measuredEntry = succeeded.Provenance.Single(entry => entry.DeviceName == "DISPLAY1");
        Assert.Equal(ExplorerGridEvidenceSource.Measured, measuredEntry.Source);
        Assert.Equal(new PixelPoint(10, 20), measuredEntry.Phase);

        var inferredEntry = succeeded.Provenance.Single(entry => entry.DeviceName == "DISPLAY2");
        Assert.Equal(ExplorerGridEvidenceSource.Inferred, inferredEntry.Source);
        Assert.Equal(new PixelPoint(30, 50), inferredEntry.Phase);
        Assert.Equal(3, inferredEntry.SampleCount);
        Assert.Equal(3, inferredEntry.ConsensusCount);
        Assert.Equal(
            new PixelPoint(30, 50),
            succeeded.Geometry!.Displays.Single(entry => entry.DeviceName == "DISPLAY2").Phase);

        // DISPLAY2 达不到门槛：不能借用 DISPLAY1 的实测事实补上缺口。
        var weakObservation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("l1", "L1", -770, 50, "DISPLAY2"),
                Icon("l2", "L2", -700, 130, "DISPLAY2"),
                Icon("l3", "L3", -650, 190, "DISPLAY2"),
                Icon("l4", "L4", -610, 210, "DISPLAY2"),
            ],
            TestSpacing);

        var failed = DesktopExplorerWorkspaceGeometryAdapter.Build(weakObservation, [fact]);

        Assert.False(failed.Succeeded);
        Assert.Contains(
            failed.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseUnavailable);
    }

    [Fact]
    public void Build_WithoutFacts_StillRejectsConflictingInferredPhases()
    {
        var displays = new[]
        {
            Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true),
            Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
        };
        var observation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("l1", "L1", -790, 20, "DISPLAY2"),
                Icon("l2", "L2", -710, 100, "DISPLAY2"),
            ],
            TestSpacing);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.False(result.Succeeded);
        Assert.Null(result.Geometry);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseConflict);
        Assert.DoesNotContain(
            result.Provenance,
            entry => entry.Source == ExplorerGridEvidenceSource.Measured);
    }

    [Fact]
    public void Build_GeometryFingerprintStillCarriesPhase_AndStaysDistinctFromTopologyIdentity()
    {
        var display = Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true);
        var phaseA = Observation(
            [display],
            [
                Icon("a", "A", 30, 50, "DISPLAY1"),
                Icon("b", "B", 110, 130, "DISPLAY1"),
                Icon("c", "C", 190, 210, "DISPLAY1"),
            ],
            TestSpacing);
        var phaseB = Observation(
            [display],
            [
                Icon("d", "D", 10, 20, "DISPLAY1"),
                Icon("e", "E", 90, 100, "DISPLAY1"),
                Icon("f", "F", 170, 180, "DISPLAY1"),
            ],
            TestSpacing);

        var first = DesktopExplorerWorkspaceGeometryAdapter.Build(phaseA);
        var second = DesktopExplorerWorkspaceGeometryAdapter.Build(phaseB);

        Assert.True(first.Succeeded && second.Succeeded, "两个观测各自都能形成可靠 phase。");
        Assert.Equal(first.TopologyIdentity, second.TopologyIdentity);
        Assert.NotEqual(first.Geometry!.GeometryFingerprint, second.Geometry!.GeometryFingerprint);
        Assert.StartsWith("explorer-geometry-v1:", first.Geometry!.GeometryFingerprint, StringComparison.Ordinal);
        Assert.StartsWith("explorer-topology-v1:", first.TopologyIdentity, StringComparison.Ordinal);
        Assert.NotEqual(first.Geometry!.GeometryFingerprint, first.TopologyIdentity);

        // 同一观测、同一事实两次 Build：plan identity 与拓扑身份都必须稳定。
        var identity = ExplorerGridTopologyIdentity.Compute(false, TestSpacing, [display]);
        var fact = Measurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(10, 20),
            identity,
            sampleCount: 3,
            consensusCount: 3,
            spacingConstant: true,
            phaseConstant: true);

        var again = DesktopExplorerWorkspaceGeometryAdapter.Build(phaseA, [fact]);
        var repeated = DesktopExplorerWorkspaceGeometryAdapter.Build(phaseA, [fact]);

        Assert.True(again.Succeeded);
        Assert.Equal(again.Geometry!.GeometryFingerprint, repeated.Geometry!.GeometryFingerprint);
        Assert.Equal(again.TopologyIdentity, repeated.TopologyIdentity);
    }

    [Fact]
    public void Build_ProvenanceDeviceNames_CorrespondOneToOneWithGeometryDisplays()
    {
        var displays = new[]
        {
            Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true),
            Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
        };
        var identity = ExplorerGridTopologyIdentity.Compute(false, TestSpacing, displays);
        var observation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("l1", "L1", -770, 50, "DISPLAY2"),
                Icon("l2", "L2", -690, 130, "DISPLAY2"),
            ],
            TestSpacing);
        var fact = Measurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(10, 20),
            identity,
            sampleCount: 3,
            consensusCount: 3,
            spacingConstant: true,
            phaseConstant: true);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation, [fact]);

        Assert.True(result.Succeeded);
        var geometry = Assert.IsType<ExplorerWorkspaceGeometry>(result.Geometry);
        Assert.Equal(geometry.Displays.Count, result.Provenance.Count);

        var fromGeometry = geometry.Displays
            .Select(entry => entry.DeviceName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var fromProvenance = result.Provenance
            .Select(entry => entry.DeviceName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(fromGeometry, fromProvenance);
    }

    private static readonly PixelPoint TestSpacing = new(80, 80);

    // Core 要求事实的时间戳是 Offset == TimeSpan.Zero 的 DateTimeOffset。
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static ExplorerGridMeasurement Measurement(
        ExplorerGridEvidenceSource source,
        string deviceName,
        PixelPoint phase,
        string topologyFingerprint,
        int sampleCount,
        int consensusCount,
        bool spacingConstant,
        bool phaseConstant)
        => new(
            source,
            deviceName,
            TestSpacing,
            phase,
            sampleCount,
            consensusCount,
            spacingConstant,
            phaseConstant,
            topologyFingerprint,
            ObservedAtUtc);

    private static DesktopObservation Observation(
        IReadOnlyList<DisplayDescriptor> displays,
        IReadOnlyList<DesktopIconDescriptor> icons,
        PixelPoint spacing)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays,
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                spacing,
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);

    private static DesktopObservation Observation(
        IReadOnlyList<DisplayDescriptor> displays,
        IReadOnlyList<DesktopIconDescriptor> icons)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays,
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);

    private static DisplayDescriptor Display(
        string deviceName,
        int left,
        int top,
        int right,
        int bottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        bool isPrimary)
        => new(
            deviceName,
            new PixelRect(left, top, right, bottom),
            new PixelRect(workLeft, workTop, workRight, workBottom),
            96,
            96,
            isPrimary);

    private static DesktopIconDescriptor Icon(
        string stableId,
        string displayName,
        int x,
        int y,
        string monitorDeviceName,
        ItemKind itemKind = ItemKind.File,
        bool isSystemItem = false)
        => new(
            stableId,
            displayName,
            ParsingName: null,
            itemKind,
            new PixelPoint(x, y),
            monitorDeviceName,
            isSystemItem,
            IsFileSystemItem: itemKind != ItemKind.SystemItem);

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }
}
