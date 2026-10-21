using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Persistence.Intelligence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private bool _workspaceReuseInProgress;

    private async void WorkspaceRebuildPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceReuseInProgress || _directApplyInProgress || _gridProbeRunning)
        {
            ShowInfo("暂时无法生成预览", "请等待当前操作完成。", InfoBarSeverity.Warning);
            return;
        }
        if ((_isLayoutEditing && _draft is not null) || _zoneColorPreferencesDirty)
        {
            ShowInfo("请先结束编辑", "保存或取消当前布局编辑，并应用或还原颜色修改后再复用设计。", InfoBarSeverity.Warning);
            return;
        }
        if (WorkspaceRevisionList.SelectedItem is not WorkspaceRevisionListItem selected)
            return;

        var blueprint = selected.Record.Blueprint;
        _workspaceReuseInProgress = true;
        WorkspaceRebuildPreviewButton.IsEnabled = false;
        try
        {
            var baseRevision = _designSession.Session.Snapshot().Revision;
            var capture = await ProductGridCalibration.Protect(_observationSource).CaptureAsync();
            if (!capture.IsCaptured || capture.Observation is null)
                throw new InvalidOperationException("没有生成预览：" + string.Join("；", capture.Issues.Select(issue => issue.Message)));
            var observation = capture.Observation;
            var geometry = DesktopExplorerWorkspaceGeometryAdapter.Build(observation,
                await _workspaceGridFactReader.TryReadActiveAsync(observation));
            if (!geometry.Succeeded || geometry.Geometry is null)
                throw new InvalidOperationException("无法建立当前桌面几何：" +
                    string.Join("；", geometry.Issues.Select(static issue => issue.Message)));

            var legacyPoliciesCompleted = blueprint.DisplayPolicies.Count == 0;
            if (legacyPoliciesCompleted)
                blueprint = DesktopWorkspaceWholeDesktopPolicy.CompleteIfAbsent(
                    DesktopWorkspaceDesignDraft.FromBlueprint(blueprint), geometry.Geometry.Displays.Select(display => display.DisplayRef))
                    .ToBlueprint(blueprint.Revision, blueprint.ParentRevision);

            var store = new SqliteDesktopSemanticUnderstandingStore(DesktopAgentDataPaths.DefaultDatabasePath);
            await store.InitializeAsync();
            var understanding = await store.GetLatestAsync()
                ?? throw new InvalidOperationException("请先在 AI 助手完成一次桌面理解，再复用此设计。");
            var bindings = await ConfirmSavedDesignBindingsAsync(blueprint, observation);
            if (bindings is null)
                return;

            blueprint = DesktopWorkspaceZonePresentation.WithLabelReservations(
                DesktopWorkspaceDesignDraft.FromBlueprint(blueprint), geometry.Geometry)
                .ToBlueprint(blueprint.Revision, blueprint.ParentRevision);

            var preview = DesktopWorkspaceSavedDesignPreviewFactory.Create(
                blueprint, understanding, observation, geometry.Geometry, bindings);
            var edit = _designSession.Session.TryApply(baseRevision,
                [new ReplaceWorkspaceDraft(DesktopWorkspaceDesignDraft.FromBlueprint(blueprint))]);
            if (!edit.Applied)
                throw new InvalidOperationException("设计在读取期间已修改，历史设计没有覆盖当前草稿。");
            PrepareWorkspaceFrozenPreviewReplacement();
            if (!TryLoadAiWorkspaceFrozenPreview(preview))
                return;
            EnableWorkspaceFrozenPreviewApply();
            WorkspaceDetailSurface.Visibility = Visibility.Collapsed;
            SetLayoutStage(DesktopLayoutStage.Execute);
            ShowInfo("已按当前桌面生成精确预览", $"复用设计版本 v{blueprint.Revision}；" +
                (legacyPoliciesCompleted ? "旧草稿未声明显示器策略，本次按全桌面参与设计补齐；空屏允许迁出图标，原保存版本未改写。" : "") +
                "请先审阅，再确认应用。尚未移动图标。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo("无法复用此设计", ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _workspaceReuseInProgress = false;
            WorkspaceRebuildPreviewButton.IsEnabled = true;
        }
    }

    private async Task<IReadOnlyList<DesktopWorkspaceCompilationItemRefBinding>?> ConfirmSavedDesignBindingsAsync(
        DesktopWorkspaceBlueprint blueprint, DesktopObservation observation)
    {
        if (blueprint.ItemOverrides.Count == 0)
            return Array.Empty<DesktopWorkspaceCompilationItemRefBinding>();

        var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = "旧设计的项目例外没有保存对应文件身份。请逐项重新选择；不会按临时编号猜测文件。取消将保留当前预览。",
            TextWrapping = TextWrapping.Wrap,
        });
        var selections = new List<(string ItemRef, ComboBox Choice)>();
        var items = observation.Icons.OrderBy(static icon => icon.DisplayName, StringComparer.Ordinal).ToArray();
        foreach (var item in blueprint.ItemOverrides)
        {
            var description = item.Kind switch
            {
                DesktopWorkspaceItemOverrideKind.Keep => "保持当前位置",
                DesktopWorkspaceItemOverrideKind.Exclude => "不参与整理",
                _ => "放入分区「" + blueprint.Zones.First(zone => zone.ZoneKey == item.ZoneKey).Name + "」",
            };
            var choice = new ComboBox { Header = $"例外 {selections.Count + 1}：{description}",
                PlaceholderText = "请选择对应桌面项目", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var icon in items)
                choice.Items.Add(new ComboBoxItem
                {
                    Content = icon.DisplayName + " · " + (icon.ParsingName ?? icon.MonitorDeviceName),
                    Tag = icon.StableId,
                });
            selections.Add((item.ItemRef, choice));
            panel.Children.Add(choice);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "重新确认项目例外", PrimaryButtonText = "确认并生成预览",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (selections.Any(static selection => selection.Choice.SelectedItem is not ComboBoxItem))
            {
                args.Cancel = true;
                dialog.Title = "请为每条例外选择对应项目";
            }
            else if (selections.Select(static selection => (string)((ComboBoxItem)selection.Choice.SelectedItem).Tag)
                         .Distinct(StringComparer.Ordinal).Count() != selections.Count)
            {
                args.Cancel = true;
                dialog.Title = "同一项目不能对应多条例外";
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return selections.Select(static selection => new DesktopWorkspaceCompilationItemRefBinding(
            selection.ItemRef, (string)((ComboBoxItem)selection.Choice.SelectedItem).Tag)).ToArray();
    }
}
