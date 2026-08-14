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
    private readonly List<DesktopCrossDisplayMigrationIntent> _aiWorkspaceMigrations = [];
    private bool _suppressAiWorkspaceSelectionChanged;
    private int _activeAiWorkspaceIndex = -1;

    private bool IsAiMultiDisplayWorkspaceActive =>
        _activeAiWorkspaceIndex >= 0 && _activeAiWorkspaceIndex < _aiWorkspaceDrafts.Count;

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
        _aiWorkspaceMigrations.Clear();

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
            $" {displaySummary}。AI 默认保持当前屏幕归属；只有用户显式迁移的项目才允许跨屏。所有草稿仍未保存、未应用。",
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

        var inboundCount = GetExplicitInboundStableIds(state.DeviceName).Count;
        LayoutCanvasStatusText.Text =
            $"AI 多屏草案 · {ShortDeviceName(state.DeviceName)} · {_draft.Zones.Count} 个分区 · " +
            $"{state.MappedAssignmentCount}/{state.SourceAssignmentCount} 个初始项目已映射" +
            (inboundCount > 0 ? $" · {inboundCount} 个显式跨屏迁入" : string.Empty) +
            " · 尚未预览";
    }

    private void PersistCurrentAiWorkspaceDraftFromEditor()
    {
        if (!IsAiMultiDisplayWorkspaceActive || _draft is null)
            return;

        var edited = BuildDraftFromEditor();
        var expectedDevice = _aiWorkspaceDrafts[_activeAiWorkspaceIndex].DeviceName;
        if (edited.TargetDisplayBinding.Kind != DesktopAgent.Core.DesktopLayouts.Templates.TargetDisplayBindingKind.ExactDeviceName
            || !string.Equals(
                edited.TargetDisplayBinding.DeviceName,
                expectedDevice,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AI 多屏工作区中的单屏草稿必须保持固定设备绑定。若要改到其他显示器，请使用显式跨屏迁移，而不是修改 TargetDisplayBinding。");
        }

        _aiWorkspaceDrafts[_activeAiWorkspaceIndex].Draft = edited;
    }

    /// <summary>
    /// 当前目标屏的显式迁入白名单。普通布局或没有迁移时为空。
    /// </summary>
    private IReadOnlyList<string> GetCurrentExplicitInboundStableIds()
    {
        if (!IsAiMultiDisplayWorkspaceActive)
            return Array.Empty<string>();

        return GetExplicitInboundStableIds(_aiWorkspaceDrafts[_activeAiWorkspaceIndex].DeviceName);
    }

    private IReadOnlyList<string> GetExplicitInboundStableIds(string targetDeviceName)
        => _aiWorkspaceMigrations
            .Where(item => string.Equals(item.TargetDeviceName, targetDeviceName, StringComparison.Ordinal))
            .Select(static item => item.StableId)
            .OrderBy(static stableId => stableId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 产品层显式跨屏迁移入口。这里只编辑 workspace Draft/RuleSet 与迁移意图，
    /// 不执行 Preview、Candidate 或 Writer。
    /// </summary>
    private void ReassignAiWorkspaceItem(
        string stableId,
        string sourceDeviceName,
        string targetDeviceName,
        Guid targetZoneId)
    {
        PersistCurrentAiWorkspaceDraftFromEditor();

        var edit = DesktopCrossDisplayWorkspaceEditor.Reassign(
            _aiWorkspaceDrafts.Select(static state => state.Draft).ToArray(),
            _aiWorkspaceMigrations,
            stableId,
            sourceDeviceName,
            targetDeviceName,
            targetZoneId);

        var draftByDevice = edit.Drafts.ToDictionary(
            static draft => draft.TargetDisplayBinding.DeviceName!,
            StringComparer.Ordinal);
        foreach (var state in _aiWorkspaceDrafts)
        {
            if (!draftByDevice.TryGetValue(state.DeviceName, out var draft))
                throw new InvalidOperationException($"跨屏编辑结果缺少显示器 {state.DeviceName} 的 Draft。");
            state.Draft = draft;
        }

        _aiWorkspaceMigrations.Clear();
        _aiWorkspaceMigrations.AddRange(edit.Migrations);

        // 任意迁移意图变化都会使现有 Preview / Apply 授权失效。
        InvalidateDirectApplyEvidence("跨屏迁移已修改 · 请重新预览");
        if (IsAiMultiDisplayWorkspaceActive)
            LoadAiWorkspaceDraft(_activeAiWorkspaceIndex);
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
        _aiWorkspaceMigrations.Clear();
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
