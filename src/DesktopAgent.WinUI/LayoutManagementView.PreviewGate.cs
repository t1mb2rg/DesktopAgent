using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private async void PreviewLayoutWithDirectApplyGate_Click(object sender, RoutedEventArgs e)
    {
        ApplyToDesktopButton.IsEnabled = false;

        try
        {
            if (!await EnsureDraftForSelectedLayoutAsync())
                return;

            var draft = BuildDraftFromEditor();
            LayoutCanvasStatusText.Text = "正在读取当前桌面…";
            var preview = await CapturePreviewAsync(draft);
            ApplyPreviewToUi(preview);
            _draft = draft;

            // 只有完整、fresh 且确实存在变化的 Preview 才开放真实 Apply 入口。
            // NoOp 继续留在 Preview 层展示，不需要为了“什么也不做”打开 Writer 确认门。
            ApplyToDesktopButton.IsEnabled =
                preview.Plan.IsComplete && preview.Plan.MoveRequiredCount > 0;

            ShowInfo("预览已刷新", "画布展示的是刚刚观察到的桌面影响。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ApplyToDesktopButton.IsEnabled = false;
            LayoutCanvasStatusText.Text = "当前无法生成预览";
            ShowError("无法预览", ex.Message);
        }
    }
}
