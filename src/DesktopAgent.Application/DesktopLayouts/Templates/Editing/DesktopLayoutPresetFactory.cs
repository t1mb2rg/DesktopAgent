using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

public enum DesktopLayoutPresetKind
{
    Full = 0,
    TwoColumns = 1,
    ThreeColumns = 2,
    FourGrid = 3,
}

/// <summary>
/// 产品层常用布局预设。只生成 Zone 元数据，不观察桌面，也不执行移动。
/// </summary>
public static class DesktopLayoutPresetFactory
{
    public static IReadOnlyList<DesktopLayoutZone> CreateZones(DesktopLayoutPresetKind preset)
    {
        if (!Enum.IsDefined(preset))
            throw new ArgumentOutOfRangeException(nameof(preset));

        return preset switch
        {
            DesktopLayoutPresetKind.Full =>
            [
                Zone("全部", 0, 0, 10_000, 10_000, 0),
            ],

            DesktopLayoutPresetKind.TwoColumns =>
            [
                Zone("左侧", 0, 0, 5_000, 10_000, 0),
                Zone("右侧", 5_000, 0, 10_000, 10_000, 1),
            ],

            DesktopLayoutPresetKind.ThreeColumns =>
            [
                Zone("左侧", 0, 0, 3_333, 10_000, 0),
                Zone("中间", 3_333, 0, 6_667, 10_000, 1),
                Zone("右侧", 6_667, 0, 10_000, 10_000, 2),
            ],

            DesktopLayoutPresetKind.FourGrid =>
            [
                Zone("左上", 0, 0, 5_000, 5_000, 0, ZonePlacementFlow.RowMajor),
                Zone("右上", 5_000, 0, 10_000, 5_000, 1, ZonePlacementFlow.RowMajor),
                Zone("左下", 0, 5_000, 5_000, 10_000, 2, ZonePlacementFlow.RowMajor),
                Zone("右下", 5_000, 5_000, 10_000, 10_000, 3, ZonePlacementFlow.RowMajor),
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
    }

    public static DesktopLayoutEditDraft CreateNewDraft(
        string name,
        DesktopLayoutPresetKind preset,
        TargetDisplayBinding? targetDisplayBinding = null)
    {
        var templateId = Guid.NewGuid();
        return new DesktopLayoutEditDraft(
            templateId,
            sourceVersion: 0,
            name,
            targetDisplayBinding ?? TargetDisplayBinding.ForPrimaryDisplay(),
            CreateZones(preset),
            DesktopLayoutIconRuleSet.Empty(templateId));
    }

    private static DesktopLayoutZone Zone(
        string name,
        int left,
        int top,
        int right,
        int bottom,
        int sortOrder,
        ZonePlacementFlow flow = ZonePlacementFlow.ColumnMajor)
        => new(
            Guid.NewGuid(),
            name,
            new NormalizedRect(left, top, right, bottom),
            sortOrder,
            flow,
            maxItemCount: null);
}
