using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopDesiredWorkspacePlanValidatorTests
{
    private static readonly Guid ObservationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void CompleteCrossDisplayPlan_WithReservedPrimaryAndFixedObstacle_IsValid()
    {
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("work", "工作区", "secondary_1", new DesktopLogicalGridRect(0, 0, 4, 4)),
            ],
            reservedRegions:
            [
                new("wallpaper", "显示壁纸", "primary", new DesktopLogicalGridRect(2, 1, 3, 3)),
            ],
            items:
            [
                Place("item-a", "secondary_1", 1, 0, "work"),
                Place("item-b", "secondary_1", 2, 0, "work"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Same(plan, result.Plan);
    }

    [Fact]
    public void PlacingIconsOnTwoDisplays_IsRejectedWithMultipleTargetDisplays()
    {
        // 执行面收口：来源可以是任意多块屏，但目标必须唯一——否则没有任何一块屏的实测网格与
        // 站定授权能覆盖整个动作。规则落在验证器里，所以不依赖任何调用方自觉。
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("main", "主屏区", "primary", new DesktopLogicalGridRect(0, 0, 4, 4)),
                new("work", "工作区", "secondary_1", new DesktopLogicalGridRect(0, 0, 4, 4)),
            ],
            items:
            [
                Place("item-a", "primary", 1, 0, "main"),
                Place("item-b", "secondary_1", 1, 1, "work"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        Assert.False(result.IsValid);
        // 两块屏各自的目标格都合法：唯一的失败原因就是「目标屏不唯一」。
        Assert.Equal(
            new[] { DesktopDesiredWorkspacePlanIssueCode.MultipleTargetDisplays },
            result.Issues.Select(static issue => issue.Code));
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.MultipleTargetDisplays, displayRef: "primary");
        var issue = Assert.Single(result.Issues);
        Assert.Contains("primary", issue.Message, StringComparison.Ordinal);
        Assert.Contains("secondary_1", issue.Message, StringComparison.Ordinal);
        Assert.Contains("目标必须唯一", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleTargetDisplay_RemainsValidEvenWhenSourcesAndKeepsSpanMultipleDisplays()
    {
        // 收口只约束「目标」，不约束「来源」：跨屏搬迁 + 其他屏保持现状仍然合法。
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("work", "工作区", "secondary_1", new DesktopLogicalGridRect(0, 0, 4, 4)),
            ],
            items:
            [
                Place("item-a", "secondary_1", 1, 0, "work"),
                Place("item-b", "secondary_1", 2, 0, "work"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        Assert.DoesNotContain(
            result.Issues,
            static issue => issue.Code == DesktopDesiredWorkspacePlanIssueCode.MultipleTargetDisplays);
    }

    [Fact]
    public void MissingUnknownAndDuplicateItems_AreAllRejectedWithoutRepair()
    {
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones: [SecondaryZone()],
            items:
            [
                Place("item-a", "secondary_1", 1, 0, "work"),
                Place("item-a", "secondary_1", 2, 0, "work"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
                DesktopDesiredWorkspaceItem.Exclude("invented-item"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        Assert.False(result.IsValid);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.DuplicateItemRef, "item-a");
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.MissingItemRef, "item-b");
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.UnknownItemRef, "invented-item");
        Assert.Same(plan, result.Plan);
        Assert.Equal(new DesktopLogicalGridCell(1, 0), plan.Items[0].TargetCell);
    }

    [Fact]
    public void TwoPlacedItemsSharingCell_AreRejectedAsCollisionAndCapacityOverflow()
    {
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("work", "单格区", "secondary_1", new DesktopLogicalGridRect(1, 1, 1, 1)),
            ],
            items:
            [
                Place("item-a", "secondary_1", 1, 1, "work"),
                Place("item-b", "secondary_1", 1, 1, "work"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.ZoneCapacityExceeded, zoneKey: "work");
    }

    [Theory]
    [InlineData(DesktopDesiredItemState.Keep)]
    [InlineData(DesktopDesiredItemState.Exclude)]
    public void StationaryItem_RemainsAnOccupiedObstacle(DesktopDesiredItemState stationaryState)
    {
        var observed = CreateObserved();
        var stationary = stationaryState == DesktopDesiredItemState.Keep
            ? DesktopDesiredWorkspaceItem.Keep("item-c")
            : DesktopDesiredWorkspaceItem.Exclude("item-c");
        var plan = CreatePlan(
            zones:
            [
                new("secondary-all", "副屏", "secondary_1", new DesktopLogicalGridRect(0, 0, 8, 6)),
            ],
            items:
            [
                Place("item-a", "secondary_1", 0, 0, "secondary-all"),
                DesktopDesiredWorkspaceItem.Keep("item-b"),
                stationary,
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision);
    }

    [Fact]
    public void FixedItemPlace_IsRejectedAndStillOccupiesItsObservedCell()
    {
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("primary-all", "主屏", "primary", new DesktopLogicalGridRect(0, 0, 8, 6)),
            ],
            items:
            [
                Place("item-a", "primary", 7, 5, "primary-all"),
                DesktopDesiredWorkspaceItem.Keep("item-b"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                Place("system-recycle-bin", "primary", 6, 5, "primary-all"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.FixedItemMoveRequested, "system-recycle-bin");
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision);
    }

    [Fact]
    public void OffGridStationaryItem_BlocksEveryOccupiedLogicalCell()
    {
        var observed = new DesktopObservedLogicalWorkspace(
            ObservationId,
            "topology-v1",
            "grid-v1",
            [new DesktopObservedLogicalDisplay("primary", true, new DesktopLogicalGridSize(4, 2))],
            [
                new DesktopObservedLogicalItem(
                    "moving",
                    "Moving.txt",
                    ItemKind.File,
                    "primary",
                    new DesktopLogicalGridCell(0, 0),
                    true),
                new DesktopObservedLogicalItem(
                    "fixed",
                    "Fixed",
                    ItemKind.SystemItem,
                    "primary",
                    new DesktopLogicalGridCell(1, 0),
                    false,
                    isGridAligned: false,
                    occupiedCells:
                    [
                        new DesktopLogicalGridCell(1, 0),
                        new DesktopLogicalGridCell(2, 0),
                    ]),
            ]);
        var plan = new DesktopDesiredWorkspacePlan(
            Guid.NewGuid(),
            ObservationId,
            "topology-v1",
            "grid-v1",
            zones:
            [
                new DesktopDesiredWorkspaceZone(
                    "all",
                    "全部",
                    "primary",
                    new DesktopLogicalGridRect(0, 0, 4, 2)),
            ],
            items:
            [
                DesktopDesiredWorkspaceItem.Place(
                    "moving",
                    "primary",
                    new DesktopLogicalGridCell(2, 0),
                    "all"),
                DesktopDesiredWorkspaceItem.Exclude("fixed"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision);
    }

    [Fact]
    public void ZoneAndReservedRegionGeometry_IsStrictlyValidated()
    {
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("left", "左侧", "primary", new DesktopLogicalGridRect(0, 0, 5, 5)),
                new("overlap", "重叠", "primary", new DesktopLogicalGridRect(4, 0, 4, 5)),
                new("outside", "越界", "secondary_1", new DesktopLogicalGridRect(7, 5, 2, 2)),
            ],
            reservedRegions:
            [
                new("wallpaper", "壁纸", "primary", new DesktopLogicalGridRect(2, 2, 3, 3)),
                new("wallpaper-2", "重复留白", "primary", new DesktopLogicalGridRect(3, 3, 2, 2)),
            ],
            items: KeepAllItems());

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.ZoneOverlap);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.ZoneOutOfBounds, zoneKey: "outside");
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.ReservedRegionOverlap);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.ZoneReservedRegionOverlap);
    }

    [Fact]
    public void PlacementMustUseKnownDisplayAndMatchingContainingZone()
    {
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("small", "小区", "secondary_1", new DesktopLogicalGridRect(0, 0, 2, 2)),
            ],
            items:
            [
                Place("item-a", "primary", 4, 4, "small"),
                Place("item-b", "missing-display", 0, 0, "missing-zone"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TargetZoneDisplayMismatch, "item-a");
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TargetCellOutsideZone, "item-a");
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.UnknownTargetDisplay, "item-b");
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.UnknownTargetZone, "item-b");
    }

    [Fact]
    public void PlacementInsideReservedRegion_IsRejected()
    {
        var observed = CreateObserved();
        var plan = CreatePlan(
            zones:
            [
                new("primary-all", "主屏", "primary", new DesktopLogicalGridRect(0, 0, 8, 6)),
            ],
            reservedRegions:
            [
                new("center", "中央留白", "primary", new DesktopLogicalGridRect(2, 2, 3, 3)),
            ],
            items:
            [
                Place("item-a", "primary", 3, 3, "primary-all"),
                DesktopDesiredWorkspaceItem.Keep("item-b"),
                DesktopDesiredWorkspaceItem.Keep("item-c"),
                DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
            ]);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.ZoneReservedRegionOverlap);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TargetCellInsideReservedRegion, "item-a");
    }

    [Fact]
    public void StalePlanIdentity_IsRejected()
    {
        var observed = CreateObserved();
        var plan = new DesktopDesiredWorkspacePlan(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "other-topology",
            "other-grid",
            items: KeepAllItems(),
            schemaVersion: DesktopDesiredWorkspacePlan.CurrentSchemaVersion + 1);

        var result = DesktopDesiredWorkspacePlanValidator.Validate(observed, plan);

        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.UnsupportedSchemaVersion);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.SourceObservationMismatch);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.TopologyFingerprintMismatch);
        AssertIssue(result, DesktopDesiredWorkspacePlanIssueCode.GridFingerprintMismatch);
    }

    [Fact]
    public void ObservedWorkspace_RejectsItemOutsideItsDisplayGrid()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DesktopObservedLogicalWorkspace(
            ObservationId,
            "topology-v1",
            "grid-v1",
            [new DesktopObservedLogicalDisplay("primary", true, new DesktopLogicalGridSize(2, 2))],
            [new DesktopObservedLogicalItem(
                "item-a",
                "A",
                ItemKind.File,
                "primary",
                new DesktopLogicalGridCell(2, 0),
                true)]));

        Assert.Contains("CurrentCell", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultGridStructs_CannotBypassOwningContractValidation()
    {
        Assert.Throws<ArgumentException>(() => new DesktopObservedLogicalDisplay(
            "primary",
            true,
            default));
        Assert.Throws<ArgumentException>(() => new DesktopDesiredWorkspaceZone(
            "zone",
            "Zone",
            "primary",
            default));
        Assert.Throws<ArgumentException>(() => new DesktopDesiredWorkspaceReservedRegion(
            "reserved",
            "留白",
            "primary",
            default));
    }

    private static DesktopObservedLogicalWorkspace CreateObserved() => new(
        ObservationId,
        "topology-v1",
        "grid-v1",
        [
            new DesktopObservedLogicalDisplay("primary", true, new DesktopLogicalGridSize(8, 6)),
            new DesktopObservedLogicalDisplay("secondary_1", false, new DesktopLogicalGridSize(8, 6)),
        ],
        [
            new DesktopObservedLogicalItem(
                "item-a", "A.txt", ItemKind.File, "primary", new DesktopLogicalGridCell(0, 0), true, "work"),
            new DesktopObservedLogicalItem(
                "item-b", "B.txt", ItemKind.File, "primary", new DesktopLogicalGridCell(1, 0), true, "work"),
            new DesktopObservedLogicalItem(
                "item-c", "C.txt", ItemKind.File, "secondary_1", new DesktopLogicalGridCell(0, 0), true, "work"),
            new DesktopObservedLogicalItem(
                "system-recycle-bin", "回收站", ItemKind.SystemItem, "primary", new DesktopLogicalGridCell(7, 5), false),
        ],
        [
            new DesktopObservedLogicalCategory("work", "工作资料", "需要集中处理的工作文件"),
        ]);

    private static DesktopDesiredWorkspacePlan CreatePlan(
        IEnumerable<DesktopDesiredWorkspaceZone>? zones = null,
        IEnumerable<DesktopDesiredWorkspaceReservedRegion>? reservedRegions = null,
        IEnumerable<DesktopDesiredWorkspaceItem>? items = null)
        => new(
            Guid.NewGuid(),
            ObservationId,
            "topology-v1",
            "grid-v1",
            zones,
            reservedRegions,
            items);

    private static DesktopDesiredWorkspaceZone SecondaryZone()
        => new("work", "工作区", "secondary_1", new DesktopLogicalGridRect(0, 0, 8, 6));

    private static DesktopDesiredWorkspaceItem Place(
        string itemRef,
        string displayRef,
        int column,
        int row,
        string zoneKey)
        => DesktopDesiredWorkspaceItem.Place(
            itemRef,
            displayRef,
            new DesktopLogicalGridCell(column, row),
            zoneKey);

    private static DesktopDesiredWorkspaceItem[] KeepAllItems() =>
    [
        DesktopDesiredWorkspaceItem.Keep("item-a"),
        DesktopDesiredWorkspaceItem.Keep("item-b"),
        DesktopDesiredWorkspaceItem.Keep("item-c"),
        DesktopDesiredWorkspaceItem.Exclude("system-recycle-bin"),
    ];

    private static void AssertIssue(
        DesktopDesiredWorkspacePlanValidationResult result,
        DesktopDesiredWorkspacePlanIssueCode code,
        string? itemRef = null,
        string? zoneKey = null,
        string? displayRef = null)
    {
        Assert.Contains(result.Issues, issue =>
            issue.Code == code &&
            (itemRef is null || string.Equals(issue.ItemRef, itemRef, StringComparison.Ordinal)) &&
            (displayRef is null || string.Equals(issue.DisplayRef, displayRef, StringComparison.Ordinal)) &&
            (zoneKey is null || string.Equals(issue.ZoneKey, zoneKey, StringComparison.Ordinal)));
    }
}
