using System.Text;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
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
                "无法打开工作区详情",
                ex.Message,
                InfoBarSeverity.Warning);
        }
    }

    private async Task OpenWorkspaceBlueprintDetailAsync(WorkspaceBlueprintLibraryItem item)
    {
        await _workspaceBlueprintStore.InitializeAsync();
        var asset = await _workspaceBlueprintStore.GetAssetAsync(item.BlueprintId)
            ?? throw new InvalidDataException($"工作区 \"{item.Name}\" 已不存在。");

        var revisions = new List<DesktopWorkspaceBlueprintRevisionRecord>(asset.CurrentRevision);
        for (var revision = 1; revision <= asset.CurrentRevision; revision++)
        {
            var record = await _workspaceBlueprintStore.GetRevisionAsync(asset.BlueprintId, revision)
                ?? throw new InvalidDataException(
                    $"工作区 \"{asset.Name}\" 缺少不可变设计版本 v{revision}。");

            if (record.Blueprint.BlueprintId != asset.BlueprintId ||
                record.Blueprint.Revision != revision)
            {
                throw new InvalidDataException(
                    $"工作区 \"{asset.Name}\" 的 v{revision} 身份与资产不一致。");
            }

            var expectedParent = revision == 1 ? (int?)null : revision - 1;
            if (record.Blueprint.ParentRevision != expectedParent)
            {
                throw new InvalidDataException(
                    $"工作区 \"{asset.Name}\" 的 v{revision} 版本链不连续。");
            }

            revisions.Add(record);
        }

        if (revisions[^1].Blueprint.Revision != asset.CurrentRevision)
        {
            throw new InvalidDataException(
                $"工作区 \"{asset.Name}\" 的当前版本指针不一致。");
        }

        _workspaceDetailAsset = asset;
        _workspaceDetailRevisions = revisions.AsReadOnly();

        var current = revisions[^1];
        WorkspaceDetailTitleText.Text = FormatWorkspaceTitle(asset.Name, current.Blueprint);
        WorkspaceRevisionList.ItemsSource = revisions
            .OrderByDescending(static record => record.Blueprint.Revision)
            .Select(record => WorkspaceRevisionListItem.From(record, asset.CurrentRevision))
            .ToArray();

        // 看设计资产属于"设计"这一步：先把阶段切过去，再让详情面占用整块地方。
        SetLayoutStage(DesktopLayoutStage.Design);
        WorkspaceLibrarySection.Visibility = Visibility.Collapsed;
        LayoutWorkbenchSurface.Visibility = Visibility.Collapsed;
        WorkspaceDetailSurface.Visibility = Visibility.Visible;
        WorkspaceRevisionList.SelectedIndex = 0;

        RenderWorkspaceRevision(current);
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
            ?? throw new InvalidOperationException("工作区详情尚未加载资产。 ");
        var blueprint = record.Blueprint;
        var isCurrent = blueprint.Revision == asset.CurrentRevision;

        WorkspaceDetailRevisionText.Text = isCurrent
            ? $"设计版本 v{blueprint.Revision} · 当前"
            : $"设计版本 v{blueprint.Revision} · 历史版本 · 只读";
        WorkspaceDetailMetaText.Text =
            $"{blueprint.DisplayPolicies.Count} 块显示器 · {blueprint.Zones.Count} 个分区 · " +
            $"{blueprint.ReservedRegions.Count} 个保留区域 · 只读设计资产";
        WorkspaceDetailSummaryText.Text = FormatBlueprintSummary(blueprint);

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
                FormatDisplayRef(policy.DisplayRef),
                FormatDisplayPolicy(policy.Policy)));
        }
    }

    private void RenderZones(DesktopWorkspaceBlueprint blueprint)
    {
        WorkspaceDetailZonesPanel.Children.Clear();
        if (blueprint.Zones.Count == 0)
        {
            WorkspaceDetailZonesPanel.Children.Add(CreateDetailMutedText("没有分区。"));
            return;
        }

        foreach (var zone in blueprint.Zones)
        {
            WorkspaceDetailZonesPanel.Children.Add(CreateDetailEntry(
                LocalizeUserFacingLabel(zone.Name),
                $"{FormatDisplayRef(zone.DisplayRef)} · {FormatAnchor(zone.Anchor)} / {FormatPacking(zone.PackingOrder)}"));
        }
    }

    private void RenderReservedRegions(DesktopWorkspaceBlueprint blueprint)
    {
        WorkspaceDetailReservedPanel.Children.Clear();
        if (blueprint.ReservedRegions.Count == 0)
        {
            WorkspaceDetailReservedPanel.Children.Add(CreateDetailMutedText("没有保留区域。"));
            return;
        }

        foreach (var region in blueprint.ReservedRegions)
        {
            WorkspaceDetailReservedPanel.Children.Add(CreateDetailEntry(
                FormatReservedPurpose(region.Purpose),
                FormatDisplayRef(region.DisplayRef)));
        }
    }

    private void RenderCategoryRules(DesktopWorkspaceBlueprint blueprint)
    {
        WorkspaceDetailCategoryRulesPanel.Children.Clear();
        if (blueprint.CategoryRules.Count == 0)
        {
            WorkspaceDetailCategoryRulesPanel.Children.Add(CreateDetailMutedText("没有分类到分区的规则。"));
            return;
        }

        var zones = blueprint.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        foreach (var rule in blueprint.CategoryRules)
        {
            var zoneName = zones.TryGetValue(rule.ZoneKey, out var zone)
                ? LocalizeUserFacingLabel(zone.Name)
                : "未知分区";
            WorkspaceDetailCategoryRulesPanel.Children.Add(CreateDetailEntry(
                FormatCategoryKey(rule.CategoryKey),
                $"分配到 {zoneName}"));
        }
    }

    private void RenderRelations(DesktopWorkspaceBlueprint blueprint)
    {
        if (blueprint.ZoneRelations.Count == 0)
        {
            WorkspaceDetailRelationsText.Text = "无显式硬空间关系。";
            return;
        }

        var zones = blueprint.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        WorkspaceDetailRelationsText.Text = string.Join(
            Environment.NewLine,
            blueprint.ZoneRelations.Select(relation =>
            {
                var source = zones.TryGetValue(relation.ZoneKey, out var sourceZone)
                    ? LocalizeUserFacingLabel(sourceZone.Name)
                    : "某分区";
                var target = zones.TryGetValue(relation.OtherZoneKey, out var targetZone)
                    ? LocalizeUserFacingLabel(targetZone.Name)
                    : "另一分区";
                return $"{source} {FormatRelation(relation.Relation)} {target}";
            }));
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
            $"指定分区 {placeInZone} · 保持原位 {keep} · 排除 {exclude}";

        WorkspaceDetailOverridesPanel.Children.Clear();
        if (blueprint.ItemOverrides.Count == 0)
        {
            WorkspaceDetailOverridesPanel.Children.Add(CreateDetailMutedText("没有项目级例外。"));
            return;
        }

        var zones = blueprint.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        for (var index = 0; index < blueprint.ItemOverrides.Count; index++)
        {
            var item = blueprint.ItemOverrides[index];
            var detail = item.Kind switch
            {
                DesktopWorkspaceItemOverrideKind.PlaceInZone =>
                    $"放入 {(item.ZoneKey is not null && zones.TryGetValue(item.ZoneKey, out var zone) ? LocalizeUserFacingLabel(zone.Name) : "指定分区")}",
                DesktopWorkspaceItemOverrideKind.Keep => "保持当前位置",
                DesktopWorkspaceItemOverrideKind.Exclude => "不参与这版设计",
                _ => "项目级例外",
            };
            WorkspaceDetailOverridesPanel.Children.Add(CreateDetailEntry($"例外项目 {index + 1}", detail));
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
            return "这版设计没有记录来源用户要求。";

        if (LooksLikeLegacyPreparationGuidance(sourceUserIntent))
        {
            return "这份开发期资产由旧版持久化逻辑创建，来源字段混入了模型编排上下文，" +
                   "因此不再把它当作用户原话展示。重新生成工作区后，这里只会保存用户实际输入的布局要求。";
        }

        return sourceUserIntent.Trim();
    }

    private static bool LooksLikeLegacyPreparationGuidance(string value)
        => value.Contains("项目 item-", StringComparison.Ordinal) &&
           value.Contains("目标屏", StringComparison.Ordinal);

    private static string FormatWorkspaceTitle(string storedName, DesktopWorkspaceBlueprint blueprint)
    {
        if (ContainsCjk(storedName))
            return storedName.Trim();

        var parts = new List<string>();
        foreach (var policy in blueprint.DisplayPolicies)
        {
            var display = FormatDisplayRef(policy.DisplayRef);
            if (policy.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve)
            {
                parts.Add($"{display}保持现状");
                continue;
            }

            var zoneCount = blueprint.Zones.Count(zone =>
                string.Equals(zone.DisplayRef, policy.DisplayRef, StringComparison.Ordinal));
            var hasReserved = blueprint.ReservedRegions.Any(region =>
                string.Equals(region.DisplayRef, policy.DisplayRef, StringComparison.Ordinal));
            if (hasReserved && zoneCount == 0)
                parts.Add($"{display}留白");
            else if (zoneCount > 0)
                parts.Add($"{display}分区整理");
        }

        return parts.Count == 0
            ? "AI 工作区设计"
            : string.Join(" · ", parts);
    }

    private static string FormatBlueprintSummary(DesktopWorkspaceBlueprint blueprint)
    {
        if (ContainsCjk(blueprint.DesignSummary))
            return blueprint.DesignSummary;

        var parts = new List<string>();
        foreach (var policy in blueprint.DisplayPolicies)
        {
            var display = FormatDisplayRef(policy.DisplayRef);
            if (policy.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve)
            {
                parts.Add($"{display}保持当前状态");
                continue;
            }

            var zoneCount = blueprint.Zones.Count(zone =>
                string.Equals(zone.DisplayRef, policy.DisplayRef, StringComparison.Ordinal));
            var reservedCount = blueprint.ReservedRegions.Count(region =>
                string.Equals(region.DisplayRef, policy.DisplayRef, StringComparison.Ordinal));
            var detail = new List<string>();
            if (zoneCount > 0)
                detail.Add($"{zoneCount} 个分区");
            if (reservedCount > 0)
                detail.Add($"{reservedCount} 个保留区域");
            parts.Add(detail.Count == 0
                ? $"{display}参与设计"
                : $"{display}包含{string.Join("和", detail)}");
        }

        return parts.Count == 0
            ? $"这版设计包含 {blueprint.Zones.Count} 个分区和 {blueprint.ReservedRegions.Count} 个保留区域。"
            : string.Join("；", parts) + "。";
    }

    private static string FormatDisplayRef(string displayRef)
        => displayRef switch
        {
            "primary" => "主屏",
            _ when displayRef.StartsWith("secondary_", StringComparison.Ordinal) =>
                $"副屏 {displayRef["secondary_".Length..]}",
            _ => "其他显示器",
        };

    private static string FormatReservedPurpose(string purpose)
    {
        if (ContainsCjk(purpose))
            return purpose.Trim();

        return purpose.Trim().ToLowerInvariant() switch
        {
            "keep the wallpaper visible" => "保留壁纸可见区域",
            "keep wallpaper visible" => "保留壁纸可见区域",
            _ => "保留空白区域",
        };
    }

    private static string LocalizeUserFacingLabel(string value)
    {
        if (ContainsCjk(value))
            return value.Trim();

        var exact = value.Trim().ToLowerInvariant() switch
        {
            "application tools" => "应用工具",
            "entertainment & games" => "娱乐与游戏",
            "study & private" => "学习与私人",
            "inbox & config" => "收件箱与配置",
            "tools" => "工具",
            "work" => "工作",
            "private" => "私人",
            "archive" => "归档",
            "entertainment" => "娱乐",
            _ => null,
        };
        if (exact is not null)
            return exact;

        var tokens = value
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return "未命名分区";

        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            var translated = TranslateToken(token);
            if (translated is null)
                return value.Trim();
            builder.Append(translated);
        }
        return builder.ToString();
    }

    private static string FormatCategoryKey(string categoryKey)
    {
        var localized = LocalizeUserFacingLabel(categoryKey);
        return string.Equals(localized, categoryKey.Trim(), StringComparison.Ordinal)
            ? "语义分类"
            : localized;
    }

    private static string? TranslateToken(string token)
        => token.Trim().ToLowerInvariant() switch
        {
            "application" => "应用",
            "applications" => "应用",
            "tool" or "tools" => "工具",
            "dev" or "development" => "开发",
            "office" => "办公",
            "communication" => "沟通",
            "network" => "网络",
            "cloud" => "云服务",
            "system" => "系统",
            "utilities" => "工具",
            "ai" => "AI",
            "assistant" or "assistants" => "助手",
            "game" or "games" => "游戏",
            "platform" or "platforms" => "平台",
            "media" => "媒体",
            "entertainment" => "娱乐",
            "study" => "学习",
            "work" => "工作",
            "competition" => "竞赛",
            "project" or "projects" => "项目",
            "personal" => "个人",
            "private" => "私人",
            "config" or "configuration" => "配置",
            "data" => "数据",
            "inbox" => "收件箱",
            "misc" or "other" => "其他",
            "unknown" => "未分类",
            "archive" => "归档",
            "and" => "与",
            _ => null,
        };

    private static bool ContainsCjk(string value)
        => value.Any(character => character is >= '\u3400' and <= '\u9FFF');

    private static string FormatDisplayPolicy(DesktopWorkspaceDisplayPolicyKind policy) => policy switch
    {
        DesktopWorkspaceDisplayPolicyKind.Design => "参与设计",
        DesktopWorkspaceDisplayPolicyKind.Preserve => "保持现状",
        _ => "未知策略",
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
        _ => "未指定",
    };

    private static string FormatRelation(DesktopWorkspaceZoneRelationKind relation) => relation switch
    {
        DesktopWorkspaceZoneRelationKind.LeftOf => "位于左侧",
        DesktopWorkspaceZoneRelationKind.RightOf => "位于右侧",
        DesktopWorkspaceZoneRelationKind.Above => "位于上方",
        DesktopWorkspaceZoneRelationKind.Below => "位于下方",
        _ => "与",
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
                $"设计版本 v{record.Blueprint.Revision}",
                record.Blueprint.Revision == currentRevision ? "当前" : "历史",
                record.CreatedAtUtc.ToLocalTime().ToString("g"),
                FormatBlueprintSummary(record.Blueprint));
    }
}
