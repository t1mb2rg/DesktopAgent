using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow
{
    private AiAssistantView? _aiAssistantView;

    private void EnsureAiAssistantView()
    {
        if (_aiAssistantView is not null)
            return;

        _aiAssistantView = new AiAssistantView();
        _aiAssistantView.LayoutDraftImportRequested += AiAssistantView_LayoutDraftImportRequested;
        _aiAssistantView.LayoutWorkspaceImportRequested += AiAssistantView_LayoutWorkspaceImportRequested;
        AiView.Children.Clear();
        Grid.SetRow(_aiAssistantView, 0);
        Grid.SetRowSpan(_aiAssistantView, 3);
        AiView.Children.Add(_aiAssistantView);
    }

    private void AiAssistantView_LayoutDraftImportRequested(
        DesktopUnderstandingLayoutDraftCompiler.CompilationResult compilation)
    {
        NavigateToLayouts();
        _layoutManagementView!.TryLoadAiImportedDraft(compilation);
    }

    private void AiAssistantView_LayoutWorkspaceImportRequested(
        DesktopUnderstandingMultiDisplayWorkspaceCompiler.WorkspaceCompilationResult workspace)
    {
        NavigateToLayouts();
        _layoutManagementView!.TryLoadAiImportedWorkspace(workspace);
    }

    private void NavigateToLayouts()
    {
        EnsureLayoutManagementView();

        var layoutNavigationItem = ShellNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                "layouts",
                StringComparison.Ordinal));

        if (layoutNavigationItem is not null)
            ShellNavigation.SelectedItem = layoutNavigationItem;

        ShowSection("layouts");
    }

    private void CancelAiAssistantRequest()
        => _aiAssistantView?.CancelActiveRequest();

    private void ReleaseAiAssistantView()
    {
        if (_aiAssistantView is null)
            return;

        _aiAssistantView.CancelActiveRequest();
        _aiAssistantView.LayoutDraftImportRequested -= AiAssistantView_LayoutDraftImportRequested;
        _aiAssistantView.LayoutWorkspaceImportRequested -= AiAssistantView_LayoutWorkspaceImportRequested;
        AiView.Children.Remove(_aiAssistantView);
        _aiAssistantView = null;
    }
}
