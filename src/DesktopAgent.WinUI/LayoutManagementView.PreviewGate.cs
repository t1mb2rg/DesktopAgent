using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private async void PreviewLayoutWithDirectApplyGate_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceFrozenPreview is not null)
        {
            RenderWorkspaceFrozenPreviewCanvas();
            RefreshApplyButtonFromCurrentEvidence();
            ShowInfo(
                "工作区精确预览仍然冻结",
                _workspaceFrozenPreviewApplyAvailable
                    ? "这份 Frozen Preview 仍是你刚刚看过的精确结果。这里不会重新读取桌面；真正点击“应用这个布局”后才会先做 fresh check。"
                    : "这份 Frozen Preview 只保留作审阅参考；它的一次性 Apply 入口已经消费或失效。请回到 AI 助手重新生成精确预览。",
                InfoBarSeverity.Informational);
            return;
        }

        ApplyToDesktopButton.IsEnabled = false;

        if (_logicalWorkspaceSession is not null)
        {
            RenderLayoutCanvas();
            ShowInfo(
                "冻结逻辑预览仍然有效",
                "这张图直接来自同一次 fresh Observation 的 ValidatedPlan。它不会重新读取桌面，也不会启动 Writer；若桌面已经变化，请回到 AI 助手重新生成。",
                InfoBarSeverity.Informational);
            return;
        }

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

            ShowInfo(
                "预览已刷新",
                $"画布展示的是刚刚观察到的桌面影响。{DescribeTargetGridPhase(preview.Plan)}",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ApplyToDesktopButton.IsEnabled = false;
            LayoutCanvasStatusText.Text = "当前无法生成预览";
            ShowError("无法预览", ex.Message);
        }
    }

    private static string DescribeTargetGridPhase(DesktopLayoutRuleAwarePreviewPlan plan)
    {
        var spacing = plan.IconSpacing;
        var phases = plan.Items
            .Where(static item => item.TargetPosition.HasValue)
            .Select(item => (
                X: PositiveModulo(item.TargetPosition!.Value.X, spacing.X),
                Y: PositiveModulo(item.TargetPosition!.Value.Y, spacing.Y)))
            .Distinct()
            .Take(2)
            .ToArray();

        return phases.Length switch
        {
            0 => " 当前没有目标坐标。",
            1 => $" 目标坐标网格相位：({phases[0].X}, {phases[0].Y})，间距：({spacing.X}, {spacing.Y})。",
            _ => " 当前目标坐标存在多组网格相位；若 Windows 开启了“将图标与网格对齐”，请先谨慎核对后再应用。",
        };
    }

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }
}
