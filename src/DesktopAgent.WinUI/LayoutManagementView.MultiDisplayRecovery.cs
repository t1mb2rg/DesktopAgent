using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    internal void PersistRecoverableStateBeforeHide()
    {
        var recoveryError = TryPersistAiMultiDisplayWorkspace();
        if (recoveryError is not null)
            LayoutCanvasStatusText.Text = $"多屏草稿恢复状态未保存：{recoveryError}";
    }

    private async Task<bool> TryRestoreAiMultiDisplayWorkspaceAsync()
    {
        DesktopLayoutWorkspaceRecoveryState? recovery;
        try
        {
            recovery = await _aiWorkspaceRecoveryStore.LoadAsync();
        }
        catch (Exception ex)
        {
            ShowInfo(
                "多屏草稿恢复失败",
                $"恢复文件已保留，但不会用于 Preview 或执行：{ex.Message}",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            return false;
        }

        if (recovery is null)
            return false;

        _aiWorkspaceDrafts.Clear();
        _aiWorkspaceDrafts.AddRange(recovery.Displays.Select(static item =>
            new AiWorkspaceDraftState(item)));
        _aiWorkspaceMigrations.Clear();
        _aiWorkspaceMigrations.AddRange(recovery.Migrations.Select(static item =>
            new DesktopCrossDisplayMigrationIntent(
                item.StableId,
                item.SourceDeviceName,
                item.TargetDeviceName,
                item.TargetZoneId,
                item.SourceRuleBeforeMigration)));

        _activeAiWorkspaceIndex = _aiWorkspaceDrafts.FindIndex(state =>
            string.Equals(state.DeviceName, recovery.ActiveDeviceName, StringComparison.Ordinal));
        if (_activeAiWorkspaceIndex < 0)
            throw new InvalidDataException("恢复工作区缺少 active display。");

        PopulateAiWorkspaceDisplaySelector(_activeAiWorkspaceIndex);
        ConfigureCrossDisplayMigrationUi();
        SetSelectedTemplateWithoutNotification(null);
        LoadAiWorkspaceDraft(_activeAiWorkspaceIndex);
        InvalidateDirectApplyEvidence("多屏草稿已恢复 · 必须重新预览");
        LayoutCanvasStatusText.Text += " · 已从恢复状态重建，必须重新预览";
        ShowInfo(
            "AI 多显示器草稿已恢复",
            $"已恢复 {_aiWorkspaceDrafts.Count} 块显示器的编辑上下文。Preview、Candidate 与 Writer 授权均未恢复，请重新预览。",
            Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
        return true;
    }

    private string? TryPersistAiMultiDisplayWorkspace()
    {
        if (!IsAiMultiDisplayWorkspaceActive || _aiWorkspaceDrafts.Count < 2)
            return null;

        try
        {
            PersistCurrentAiWorkspaceDraftFromEditor();
            var activeDeviceName = _aiWorkspaceDrafts[_activeAiWorkspaceIndex].DeviceName;
            var displays = _aiWorkspaceDrafts.Select(static state =>
                new DesktopLayoutWorkspaceDraftRecoveryState(
                    state.Display,
                    state.DisplayRef,
                    state.ObservedIconCount,
                    state.SourceAssignmentCount,
                    state.MappedAssignmentCount,
                    state.Draft.TemplateId,
                    state.Draft.SourceVersion,
                    state.Draft.Name,
                    state.Draft.Zones,
                    state.Draft.RuleSet,
                    state.Draft.ExplicitInboundStableIds)).ToArray();
            var migrations = _aiWorkspaceMigrations.Select(static item =>
                new DesktopLayoutWorkspaceMigrationRecoveryState(
                    item.StableId,
                    item.SourceDeviceName,
                    item.TargetDeviceName,
                    item.TargetZoneId,
                    item.SourceRuleBeforeMigration)).ToArray();

            var recovery = new DesktopLayoutWorkspaceRecoveryState(
                activeDeviceName,
                displays,
                migrations);
            _aiWorkspaceRecoveryStore.SaveAsync(recovery).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void DeleteAiMultiDisplayWorkspaceRecovery()
    {
        try
        {
            _aiWorkspaceRecoveryStore.DeleteAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // A stale draft-recovery file never grants execution authority. If deletion fails,
            // the next load still validates the full payload and requires a fresh Preview.
        }
    }

    private void PopulateAiWorkspaceDisplaySelector(int selectedIndex)
    {
        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.Items.Clear();
            foreach (var state in _aiWorkspaceDrafts)
                AiWorkspaceDisplayCombo.Items.Add(state.DisplayText);

            AiWorkspaceDisplayCombo.SelectedIndex = selectedIndex;
            AiWorkspaceDisplayCombo.Visibility = _aiWorkspaceDrafts.Count > 1
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }
    }

    private DesktopLayoutEditDraft RehydrateSavedDraft(
        DesktopLayoutTemplate template,
        DesktopAgent.Core.DesktopLayouts.Templates.Rules.DesktopLayoutIconRuleSet ruleSet,
        DesktopLayoutEditDraft previousDraft)
    {
        var explicitInbound = IsAiMultiDisplayWorkspaceActive &&
            previousDraft.TemplateId == template.TemplateId
                ? previousDraft.ExplicitInboundStableIds
                : Array.Empty<string>();

        var saved = new DesktopLayoutEditDraft(
            template.TemplateId,
            template.Version,
            template.Name,
            template.TargetDisplayBinding,
            template.Zones,
            ruleSet,
            explicitInbound);

        if (IsAiMultiDisplayWorkspaceActive)
            _aiWorkspaceDrafts[_activeAiWorkspaceIndex].Draft = saved;

        return saved;
    }
}
