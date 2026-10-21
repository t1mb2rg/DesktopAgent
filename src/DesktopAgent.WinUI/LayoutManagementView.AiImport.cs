using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    /// <summary>
    /// 把已经由本地编译器验证过的 AI 草案装入现有布局工作台。
    /// 这里只替换内存编辑草稿，不保存模板、不设置当前布局，也不调用 Writer。
    /// </summary>
    internal bool TryLoadAiImportedDraft(
        DesktopUnderstandingLayoutDraftCompiler.CompilationResult compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        if (_isLayoutEditing && _draft is not null)
        {
            ShowInfo(
                "已有布局正在编辑",
                "AI 草案没有覆盖当前未保存修改。请先保存或取消当前编辑，再从 AI 助手重新导入。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (_zoneColorPreferencesDirty)
        {
            ShowInfo(
                "分区颜色尚未应用",
                "AI 草案没有覆盖当前颜色修改。请先应用或还原颜色，再重新导入。",
                InfoBarSeverity.Warning);
            return false;
        }

        SetSelectedTemplateWithoutNotification(null);
        _draft = compilation.Draft;
        LoadDraftIntoEditor(_draft);
        SetLayoutInteractionMode(isEditing: true);

        LayoutCanvasStatusText.Text =
            $"AI 草案 · {_draft.Zones.Count} 个分区 · " +
            $"{compilation.MappedAssignmentCount}/{compilation.SourceAssignmentCount} 个项目已映射 · 尚未预览";

        var mappingText = compilation.UnmappedAssignmentCount == 0
            ? $"已把 {compilation.MappedAssignmentCount} 个 AI Assignment 转换为 ZoneOverride 规则。"
            : $"已映射 {compilation.MappedAssignmentCount} 个项目，{compilation.UnmappedAssignmentCount} 个项目仍需人工确认。";

        ShowInfo(
            "AI 草案已导入",
            $"{mappingText} 当前草案尚未保存、尚未应用，也没有移动任何桌面图标。",
            InfoBarSeverity.Success);
        return true;
    }
}
