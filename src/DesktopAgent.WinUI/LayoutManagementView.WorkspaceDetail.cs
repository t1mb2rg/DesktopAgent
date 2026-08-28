using System.Text;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private async void WorkspaceBlueprintList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WorkspaceBlueprintLibraryItem item)
            return;

        try
        {
            await ShowWorkspaceBlueprintDetailAsync(item);
        }
        catch (Exception ex)
        {
            ShowInfo(
                "无法打开 Workspace 详情",
                ex.Message,
                InfoBarSeverity.Warning);
        }
    }

    private async Task ShowWorkspaceBlueprintDetailAsync(WorkspaceBlueprintLibraryItem item)
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

        var current = revisions[^1];
        if (current.Blueprint.Revision != asset.CurrentRevision)
        {
            throw new InvalidDataException(
                $"Workspace \"{asset.Name}\" 的 current revision 指针不一致。");
        }

        var dialog = new ContentDialog
        {
            Title = $"Workspace · {asset.Name}",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = WorkspaceLibrarySection.XamlRoot,
            Content = BuildWorkspaceBlueprintDetailContent(asset, current, revisions),
        };

        await dialog.ShowAsync();
    }

    private static ScrollViewer BuildWorkspaceBlueprintDetailContent(
        DesktopWorkspaceBlueprintAssetSummary asset,
        DesktopWorkspaceBlueprintRevisionRecord current,
        IReadOnlyList<DesktopWorkspaceBlueprintRevisionRecord> revisions)
    {
        var blueprint = current.Blueprint;
        var content = new StackPanel
        {
            Spacing = 14,
            MinWidth = 620,
            MaxWidth = 820,
        };

        content.Children.Add(CreateWorkspaceDetailLead(
            $"Blueprint v{blueprint.Revision} · 当前",
            blueprint.DesignSummary));
        content.Children.Add(CreateWorkspaceDetailSection(
            "显示器策略",
            FormatDisplayPolicies(blueprint)));
        content.Children.Add(CreateWorkspaceDetailSection(
            "Zones",
            FormatZones(blueprint)));
        content.Children.Add(CreateWorkspaceDetailSection(
            "Reserved Regions",
            FormatReservedRegions(blueprint)));
        content.Children.Add(CreateWorkspaceDetailSection(
            "Category → Zone",
            FormatCategoryRules(blueprint)));
        content.Children.Add(CreateWorkspaceDetailSection(
            "空间关系与例外",
            FormatRelationsAndOverrides(blueprint)));
        content.Children.Add(CreateWorkspaceDetailSection(
            "设计来源",
            current.SourceUserIntent ?? "这版 Blueprint 没有记录来源用户意图。"));
        content.Children.Add(CreateWorkspaceDetailSection(
            "版本历史",
            FormatRevisionHistory(asset, revisions)));

        return new ScrollViewer
        {
            MaxHeight = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = content,
        };
    }

    private static Border CreateWorkspaceDetailLead(string title, string summary)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
        });
        panel.Children.Add(new TextBlock
        {
            Text = summary,
            FontSize = 13,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "这是 durable design asset。打开详情不会恢复 Frozen Preview，也不会获得执行权限。",
            FontSize = 11,
            Opacity = 0.52,
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Child = panel,
        };
    }

    private static StackPanel CreateWorkspaceDetailSection(string title, string body)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static string FormatDisplayPolicies(DesktopWorkspaceBlueprint blueprint)
        => string.Join(
            Environment.NewLine,
            blueprint.DisplayPolicies.Select(policy =>
                $"{policy.DisplayRef} · {FormatDisplayPolicy(policy.Policy)}"));

    private static string FormatZones(DesktopWorkspaceBlueprint blueprint)
    {
        if (blueprint.Zones.Count == 0)
            return "无 Zone";

        return string.Join(
            Environment.NewLine,
            blueprint.Zones.Select(zone =>
                $"{zone.Name} · {zone.DisplayRef} · {FormatAnchor(zone.Anchor)} / {FormatPacking(zone.PackingOrder)}"));
    }

    private static string FormatReservedRegions(DesktopWorkspaceBlueprint blueprint)
    {
        if (blueprint.ReservedRegions.Count == 0)
            return "无 Reserved Region";

        return string.Join(
            Environment.NewLine,
            blueprint.ReservedRegions.Select(region =>
                $"{region.Purpose} · {region.DisplayRef}"));
    }

    private static string FormatCategoryRules(DesktopWorkspaceBlueprint blueprint)
    {
        if (blueprint.CategoryRules.Count == 0)
            return "没有 Category → Zone 规则";

        var zones = blueprint.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        return string.Join(
            Environment.NewLine,
            blueprint.CategoryRules.Select(rule =>
                $"{rule.CategoryKey} → {(zones.TryGetValue(rule.ZoneKey, out var zone) ? zone.Name : rule.ZoneKey)}"));
    }

    private static string FormatRelationsAndOverrides(DesktopWorkspaceBlueprint blueprint)
    {
        var builder = new StringBuilder();
        if (blueprint.ZoneRelations.Count == 0)
        {
            builder.Append("空间关系：无显式硬关系");
        }
        else
        {
            builder.AppendLine("空间关系：");
            foreach (var relation in blueprint.ZoneRelations)
            {
                builder.Append("  ")
                    .Append(relation.ZoneKey)
                    .Append(' ')
                    .Append(FormatRelation(relation.Relation))
                    .Append(' ')
                    .AppendLine(relation.OtherZoneKey);
            }
        }

        var placeInZone = blueprint.ItemOverrides.Count(static item =>
            item.Kind == DesktopWorkspaceItemOverrideKind.PlaceInZone);
        var keep = blueprint.ItemOverrides.Count(static item =>
            item.Kind == DesktopWorkspaceItemOverrideKind.Keep);
        var exclude = blueprint.ItemOverrides.Count(static item =>
            item.Kind == DesktopWorkspaceItemOverrideKind.Exclude);

        if (builder.Length > 0 && builder[^1] != '\n')
            builder.AppendLine();
        builder.Append($"Item Overrides：PlaceInZone {placeInZone} · Keep {keep} · Exclude {exclude}");
        return builder.ToString().TrimEnd();
    }

    private static string FormatRevisionHistory(
        DesktopWorkspaceBlueprintAssetSummary asset,
        IReadOnlyList<DesktopWorkspaceBlueprintRevisionRecord> revisions)
    {
        var builder = new StringBuilder();
        foreach (var revision in revisions.OrderByDescending(static record => record.Blueprint.Revision))
        {
            var blueprint = revision.Blueprint;
            builder.Append('v').Append(blueprint.Revision);
            if (blueprint.Revision == asset.CurrentRevision)
                builder.Append(" · 当前");
            builder.Append(" · ")
                .AppendLine(revision.CreatedAtUtc.ToLocalTime().ToString("g"));
            builder.Append("  ").AppendLine(blueprint.DesignSummary);
            builder.Append("  ").AppendLine(
                blueprint.ParentRevision is int parent
                    ? $"基于 v{parent}"
                    : "初始版本");
        }
        return builder.ToString().TrimEnd();
    }

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
}
