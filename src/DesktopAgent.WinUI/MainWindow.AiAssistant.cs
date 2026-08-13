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
        AiView.Children.Clear();
        Grid.SetRow(_aiAssistantView, 0);
        Grid.SetRowSpan(_aiAssistantView, 3);
        AiView.Children.Add(_aiAssistantView);
    }

    private void CancelAiAssistantRequest()
        => _aiAssistantView?.CancelActiveRequest();

    private void ReleaseAiAssistantView()
    {
        if (_aiAssistantView is null)
            return;

        _aiAssistantView.CancelActiveRequest();
        AiView.Children.Remove(_aiAssistantView);
        _aiAssistantView = null;
    }
}
