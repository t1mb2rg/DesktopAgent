using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private DesktopWorkspaceBlueprintAssetSummary? _workspaceDetailAsset;
    private IReadOnlyList<DesktopWorkspaceBlueprintRevisionRecord> _workspaceDetailRevisions = [];

    private async void WorkspaceBlueprintList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WorkspaceBlueprintLibraryItem item)
            return;

        try
        {
            await OpenWorkspaceBlueprintDetailAsync(item);
        }
        catch (Exception ex)
        {
            ShowInfo(
                "无法打开 Workspace 详情",
                ex.Message,
                InfoBarSeverity.Warning);
        }
    }

    private async Task OpenWorkspaceBlueprintDetailAsync(WorkspaceBlueprintLibraryItem item)
    {
        await _workspaceBlueprintStore.InitializeAsync();
        var asset = await _workspaceBlueprintStore.GetAssetAsync(item.BlueprintId)
            ?? throw new InvalidDataException($"Workspace \"{item.Name}\" 已不存在。");

        var revisions = new List<DesktopWorkspaceBlueprintRevisionRecord>(asset.CurrentRevision);
        for (var revision = 1; revision <= asset.CurrentRevision; revision++)
        {
            var record = await _workspaceBlueprintStore.GetRevisionAsync(asset.BlueprintId, revision)
                ?? throw new InvalidDataException(
                    $"Workspace \"{asset.Name}\" 缺少 immutable Blueprint v{revision}。");

            if (record.Blueprint.BlueprintId != asset.BlueprintId ||
                record.Blueprint.Revision != revision)
            {
                throw new InvalidDataException(
                    $"Workspace \"{asset.Name}\" 的 v{revision} identity 与资产不一致。");
            }

            var expectedParent = revision == 1 ? (int?)null : revision - 1;
            if (record.Blueprint.ParentRevision != expectedParent)
            {
                throw new InvalidDataException(
                    $"Workspace \"{asset.Name}\" 的 v{revision} revision chain 不连续。");
            }

            revisions.Add(record);
        }

        if (revisions[^1].Blueprint.Revision != asset.CurrentRevision)
        {
            throw new InvalidDataException(
                $"Workspace \"{asset.Name}\" 的 current revision 指针不一致。");
        }

        _workspaceDetailAsset = asset;
        _workspaceDetailRevisions = revisions.AsReadOnly();

        WorkspaceDetailTitleText.Text = asset.Name;
        WorkspaceRevisionList.ItemsSource = revisions
            .OrderByDescending(static record => record.Blueprint.Revision)
            .Select(record => WorkspaceRevisionListItem.From(record, asset.CurrentRevision))
            .ToArray();

        WorkspaceLibrarySection.Visibility = Visibility.Collapsed;
        LayoutWorkbenchSurface.Visibility = Visibility.Collapsed;
        WorkspaceDetailSurface.Visibility = Visibility.Visible;
        WorkspaceRevisionList.SelectedIndex = 0;

        RenderWorkspaceRevision(revisions[^1]);
    }

    private void WorkspaceDetailBack_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceDetailSurface.Visibility = Visibility.Collapsed;
        WorkspaceLibrarySection.Visibility = Visibility.Visible;
        LayoutWorkbenchSurface.Visibility = Visibility.Visible;
        WorkspaceRevisionList.ItemsSource = null;
        _workspaceDetailAsset = null;
        _workspaceDetailRevisions = [];
    }

    private void WorkspaceRevisionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceRevisionList.SelectedItem is not WorkspaceRevisionListItem selected)
            return;

        RenderWorkspaceRevision(selected.Record);
    }

    private void RenderWorkspaceRevision(DesktopWorkspaceBlueprintRevisionRecord record)
    {
        var asset = _workspaceDetailAsset
            ?? throw new InvalidOperationException("Workspace Detail 尚未加载资产。 ");
        var blueprint = record.Blueprint;
        var isCurrent = blueprint.Revision == asset.CurrentRevision;

        WorkspaceDetailRevisionText.Text = isCurrent
            ? $"Blueprint v{blueprint.Revision} · 当前"
            : $"Blueprint v{blueprint.Revision} · 历史版本 · 只读";
        WorkspaceDetailMetaText.Text =
            $"{blueprint.DisplayPolicies.Count} 块显示器 · {blueprint.Zones.Count} Zones · " +
            $"{blueprint.ReservedRegions.Count} Reserved · 只读设计资产";
        WorkspaceDetailSummaryText.Text = blueprint.DesignSummary;

        RenderDisplayPolicies(blueprint);
        RenderZones(blueprint);
        RenderReservedRegions(blueprint);
        RenderCategoryRules(blueprint);
        RenderRelations(blueprint);
        RenderItemOverrides(blueprint);
        WorkspaceDetailSourceIntentText.Text = FormatSourceUserIntent(record.SourceUserIntent);
    }

    private void RenderDisplayPolicies(DesktopWorkspaceBlueprint blueprint)
    {
        WorkspaceDetailDisplayPoliciesPanel.Children.Clear();
        if (blueprint.DisplayPolicies.Count == 0)
        {
            WorkspaceDetailDisplayPoliciesPanel.Children.Add(CreateDetailMutedText("没有声明显示器策略。"));
            return;
        }

        foreach (var policy in blueprint.DisplayPolicies)
        {
            WorkspaceDetailDisplayPoliciesPanel.Children.Add(CreateDetailEntry(
                policy.DisplayRef,
                FormatDisplayPolicy(policy.Policy)));
        }
    }

    private void RenderZones(DesktopWorkspaceBlueprint blueprint)
    {
        WorkspaceDetailZonesPanel.Children.Clear();
        if (blueprint.Zones.Count == 0)
        {
            WorkspaceDetailZonesPanel.Children.Add(CreateDetailMutedText("没有 Zone。"));
            return;
        }

        foreach (var zone in blueprint.Zones)
        {
            WorkspaceDetailZonesPanel.Children.Add(CreateDetailEntry(
                zone.Name,
                $"{zone.DisplayRef} · {FormatAnchor(zone.Anchor)} / {FormatPacking(zone.PackingOrder)}"));
        }
    }

    private void RenderReservedRegions(DesktopWorkspaceBlueprint blueprint)
    {
        WorkspaceDetailReservedPanel.Children.Clear();
        if (blueprint.ReservedRegions.Count == 0)
        {
            WorkspaceDetailReservedPanel.Children.Add(CreateDetailMutedText("没有 Reserved Region。"));
            return;
        }

        foreach (var region in blueprint.ReservedRegions)
        {
            WorkspaceDetailReservedPanel.Children.Add(CreateDetailEntry(
                region.Purpose,
                region.DisplayRef));
        }
    }

    private void RenderCategoryRules(DesktopWorkspaceBlueprint blueprint)
    {
        WorkspaceDetailCategoryRulesPanel.Children.Clear();
        if (blueprint.CategoryRules.Count == 0)
        {
            WorkspaceDetailCategoryRulesPanel.Children.Add(CreateDetailMutedText("没有 Category → Zone 规则。"));
            return;
        }

        var zones = blueprint.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        foreach (var rule in blueprint.CategoryRules)
        {
            var zoneName = zones.TryGetValue(rule.ZoneKey, out var zone)
                ? zone.Name
                : rule.ZoneKey;
            WorkspaceDetailCategoryRulesPanel.Children.Add(CreateDetailEntry(rule.CategoryKey, zoneName));
        }
    }

    private void RenderRelations(DesktopWorkspaceBlueprint blueprint)
    {
        if (blueprint.ZoneRelations.Count == 0)
        {
            WorkspaceDetailRelationsText.Text = "无显式硬空间关系。";
            return;
        }

        WorkspaceDetailRelationsText.Text = string.Join(
            Environment.NewLine,
            blueprint.ZoneRelations.Select(relation =>
                $"{relation.ZoneKey} {FormatRelation(relation.Relation)} {relation.OtherZoneKey}"));
    }

    private void RenderItemOverrides(DesktopWorkspaceBlueprint blueprint)
    {
        var placeInZone = blueprint.ItemOverrides.Count(static item =>
            item.Kind == DesktopWorkspaceItemOverrideKind.PlaceInZone);
        var keep = blueprint.ItemOverrides.Count(static item =>
            item.Kind == DesktopWorkspaceItemOverrideKind.Keep);
        var exclude = blueprint.ItemOverrides.Count(static item =>
            item.Kind == DesktopWorkspaceItemOverrideKind.Exclude);
        WorkspaceDetailOverridesSummaryText.Text =
            $"PlaceInZone {placeInZone} · Keep {keep} · Exclude {exclude}";

        WorkspaceDetailOverridesPanel.Children.Clear();
        if (blueprint.ItemOverrides.Count == 0)
        {
            WorkspaceDetailOverridesPanel.Children.Add(CreateDetailMutedText("没有 item-level 例外。"));
            return;
        }

        foreach (var item in blueprint.ItemOverrides)
        {
            var detail = item.Kind == DesktopWorkspaceItemOverrideKind.PlaceInZone
                ? $"PlaceInZone → {item.ZoneKey}"
                : item.Kind.ToString();
            WorkspaceDetailOverridesPanel.Children.Add(CreateDetailEntry(item.ItemRef, detail));
        }
    }

    private static StackPanel CreateDetailEntry(string title, string detail)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Opacity = 0.56,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static TextBlock CreateDetailMutedText(string text)
        => new()
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.56,
            TextWrapping = TextWrapping.Wrap,
        };

    private static string FormatSourceUserIntent(string? sourceUserIntent)
    {
        if (string.IsNullOrWhiteSpace(sourceUserIntent))
            return "这版 Blueprint 没有记录来源用户意图。";

        if (LooksLikeLegacyPreparationGuidance(sourceUserIntent))
        {
            return "这份开发期资产由旧版持久化逻辑创建，来源字段包含 Provider 编排上下文，" +
                   "因此不再把它当作用户原话展示。重新生成 Workspace 后这里只会保存用户实际输入的布局要求。";
        }

        return sourceUserIntent.Trim();
    }

    private static bool LooksLikeLegacyPreparationGuidance(string value)
        => value.Contains("项目 item-", StringComparison.Ordinal) &&
           value.Contains("目标屏", StringComparison.Ordinal);

    private static string FormatDisplayPolicy(DesktopWorkspaceDisplayPolicyKind policy) => policy switch
    {
        DesktopWorkspaceDisplayPolicyKind.Design => "设计",
        DesktopWorkspaceDisplayPolicyKind.Preserve => "保持现状",
        _ => policy.ToString(),
    };

    private static string FormatAnchor(DesktopWorkspaceAnchorKind anchor) => anchor switch
    {
        DesktopWorkspaceAnchorKind.TopLeft => "左上",
        DesktopWorkspaceAnchorKind.TopRight => "右上",
        DesktopWorkspaceAnchorKind.BottomLeft => "左下",
        DesktopWorkspaceAnchorKind.BottomRight => "右下",
        DesktopWorkspaceAnchorKind.Center => "居中",
        _ => "未指定",
    };

    private static string FormatPacking(DesktopWorkspacePackingOrder packing) => packing switch
    {
        DesktopWorkspacePackingOrder.RowMajor => "行优先",
        DesktopWorkspacePackingOrder.ColumnMajor => "列优先",
        _ => packing.ToString(),
    };

    private static string FormatRelation(DesktopWorkspaceZoneRelationKind relation) => relation switch
    {
        DesktopWorkspaceZoneRelationKind.LeftOf => "位于左侧",
        DesktopWorkspaceZoneRelationKind.RightOf => "位于右侧",
        DesktopWorkspaceZoneRelationKind.Above => "位于上方",
        DesktopWorkspaceZoneRelationKind.Below => "位于下方",
        _ => relation.ToString(),
    };

    private sealed record WorkspaceRevisionListItem(
        DesktopWorkspaceBlueprintRevisionRecord Record,
        string RevisionText,
        string StateText,
        string CreatedText,
        string Summary)
    {
        internal static WorkspaceRevisionListItem From(
            DesktopWorkspaceBlueprintRevisionRecord record,
            int currentRevision)
            => new(
                record,
                $"Blueprint v{record.Blueprint.Revision}",
                record.Blueprint.Revision == currentRevision ? "当前" : "历史",
                record.CreatedAtUtc.ToLocalTime().ToString("g"),
                record.Blueprint.DesignSummary);
    }
}
