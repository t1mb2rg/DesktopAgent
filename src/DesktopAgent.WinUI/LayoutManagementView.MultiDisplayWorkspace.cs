using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private sealed class AiWorkspaceDraftState
    {
        public string DeviceName { get; }
        public bool IsPrimary { get; }
        public int ObservedIconCount { get; }
        public int SourceAssignmentCount { get; }
        public int MappedAssignmentCount { get; }
        public DesktopLayoutEditDraft Draft { get; set; }

        public AiWorkspaceDraftState(
            DesktopUnderstandingMultiDisplayWorkspaceCompiler.DisplayCompilation compilation)
        {
            DeviceName = compilation.Display.DeviceName;
            IsPrimary = compilation.Display.IsPrimary;
            ObservedIconCount = compilation.ObservedIconCount;
            SourceAssignmentCount = compilation.SourceAssignmentCount;
            MappedAssignmentCount = compilation.MappedAssignmentCount;
            Draft = compilation.Draft;
        }

        public string DisplayText =>
            $"{(IsPrimary ? "主屏" : "副屏")} · {ShortDeviceName(DeviceName)} · {SourceAssignmentCount} 项";
    }

    private readonly List<AiWorkspaceDraftState> _aiWorkspaceDrafts = [];
    private bool _suppressAiWorkspaceSelectionChanged;
    private int _activeAiWorkspaceIndex = -1;

    internal bool TryLoadAiImportedWorkspace(
        DesktopUnderstandingMultiDisplayWorkspaceCompiler.WorkspaceCompilationResult workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (_isLayoutEditing && _draft is not null)
        {
            ShowInfo(
                "已有布局正在编辑",
                "AI 多屏工作区没有覆盖当前未保存修改。请先保存或取消当前编辑，再从 AI 助手重新导入。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (_zoneColorPreferencesDirty)
        {
            ShowInfo(
                "分区颜色尚未应用",
                "AI 多屏工作区没有覆盖当前颜色修改。请先应用或还原颜色，再重新导入。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (workspace.Displays.Count == 0)
        {
            ShowInfo(
                "没有可导入的显示器草稿",
                "本次 AI 结果没有生成任何安全的单显示器布局草稿。",
                InfoBarSeverity.Warning);
            return false;
        }

        _aiWorkspaceDrafts.Clear();
        _aiWorkspaceDrafts.AddRange(workspace.Displays.Select(static compilation =>
            new AiWorkspaceDraftState(compilation)));

        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.Items.Clear();
            foreach (var state in _aiWorkspaceDrafts)
                AiWorkspaceDisplayCombo.Items.Add(state.DisplayText);

            AiWorkspaceDisplayCombo.SelectedIndex = 0;
            AiWorkspaceDisplayCombo.Visibility = _aiWorkspaceDrafts.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }

        _activeAiWorkspaceIndex = 0;
        SetSelectedTemplateWithoutNotification(null);
        LoadAiWorkspaceDraft(_activeAiWorkspaceIndex);

        var displaySummary = string.Join(
            " · ",
            _aiWorkspaceDrafts.Select(state =>
                $"{ShortDeviceName(state.DeviceName)} {state.MappedAssignmentCount}/{state.SourceAssignmentCount}"));
        ShowInfo(
            "AI 多显示器草案已导入",
            $"{workspace.Displays.Count} 块显示器 · {workspace.MappedAssignmentCount}/{workspace.SourceAssignmentCount} 个 Assignment 已映射。" +
            $" {displaySummary}。所有草稿仍未保存、未应用，切换显示器不会移动桌面图标。",
            InfoBarSeverity.Success);
        return true;
    }

    private void AiWorkspaceDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAiWorkspaceSelectionChanged)
            return;

        var targetIndex = AiWorkspaceDisplayCombo.SelectedIndex;
        if (targetIndex < 0 || targetIndex >= _aiWorkspaceDrafts.Count)
            return;
        if (targetIndex == _activeAiWorkspaceIndex)
            return;

        if (_zoneColorPreferencesDirty)
        {
            RestoreAiWorkspaceSelection();
            ShowInfo(
                "分区颜色尚未应用",
                "先应用或还原当前显示器的颜色修改，再切换到另一块显示器。",
                InfoBarSeverity.Informational);
            return;
        }

        try
        {
            PersistCurrentAiWorkspaceDraftFromEditor();
            _activeAiWorkspaceIndex = targetIndex;
            LoadAiWorkspaceDraft(targetIndex);
        }
        catch (Exception ex)
        {
            RestoreAiWorkspaceSelection();
            ShowError("无法切换显示器草稿", ex.Message);
        }
    }

    private void LoadAiWorkspaceDraft(int index)
    {
        var state = _aiWorkspaceDrafts[index];
        _draft = state.Draft;
        LoadDraftIntoEditor(_draft);
        SetLayoutInteractionMode(isEditing: true);

        // workspace 内每份 Draft 的物理屏幕身份由本地分桶确定。
        // 用户通过上方 selector 切屏，而不是直接改 TargetDisplayBinding。
        LayoutTargetBindingCombo.IsEnabled = false;
        LayoutDeviceNameTextBox.IsReadOnly = true;

        LayoutCanvasStatusText.Text =
            $"AI 多屏草案 · {ShortDeviceName(state.DeviceName)} · {_draft.Zones.Count} 个分区 · " +
            $"{state.MappedAssignmentCount}/{state.SourceAssignmentCount} 个项目已映射 · 尚未预览";
    }

    private void PersistCurrentAiWorkspaceDraftFromEditor()
    {
        if (_activeAiWorkspaceIndex < 0
            || _activeAiWorkspaceIndex >= _aiWorkspaceDrafts.Count
            || _draft is null)
        {
            return;
        }

        var edited = BuildDraftFromEditor();
        var expectedDevice = _aiWorkspaceDrafts[_activeAiWorkspaceIndex].DeviceName;
        if (edited.TargetDisplayBinding.Kind != DesktopAgent.Core.DesktopLayouts.Templates.TargetDisplayBindingKind.ExactDeviceName
            || !string.Equals(
                edited.TargetDisplayBinding.DeviceName,
                expectedDevice,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AI 多屏工作区中的单屏草稿必须保持固定设备绑定。若要改到其他显示器，请切换工作区显示器，而不是修改 TargetDisplayBinding。");
        }

        _aiWorkspaceDrafts[_activeAiWorkspaceIndex].Draft = edited;
    }

    private void RestoreAiWorkspaceSelection()
    {
        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.SelectedIndex = _activeAiWorkspaceIndex;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }
    }

    internal void ClearAiMultiDisplayWorkspace()
    {
        _aiWorkspaceDrafts.Clear();
        _activeAiWorkspaceIndex = -1;
        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.Items.Clear();
            AiWorkspaceDisplayCombo.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }

        if (_isLayoutEditing)
        {
            LayoutTargetBindingCombo.IsEnabled = true;
            LayoutDeviceNameTextBox.IsReadOnly = false;
        }
    }

    private static string ShortDeviceName(string deviceName)
    {
        const string prefix = @"\\.\";
        return deviceName.StartsWith(prefix, StringComparison.Ordinal)
            ? deviceName[prefix.Length..]
            : deviceName;
    }
}
