using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

/// <summary>
/// 「撤销上次整理」的产品入口。
///
/// 点一次先做只读核对（<c>PrepareAsync</c>）：没有可撤销的整理、检查点丢了、
/// 桌面自上次整理后被改过——一律说清原因并停住，绝不动桌面。
/// 核对通过才弹确认框，然后走与「应用到桌面」完全相同的受控执行入口。
/// </summary>
public sealed partial class LayoutManagementView
{
    private DesktopLayoutRollbackService? _rollbackService;
    private bool _rollbackInProgress;

    private DesktopLayoutRollbackService RollbackService =>
        _rollbackService ??= new DesktopLayoutRollbackService(
            _directApplyRecoveryStore,
            _directApplySnapshotStore,
            _directApplyRecoveryStore,
            _workspaceExecutionJournalStore,
            _workspaceDynamicExecutionJournalStore);

    private async void RollbackLastApply_Click(object sender, RoutedEventArgs e)
    {
        if (_rollbackInProgress)
            return;

        _rollbackInProgress = true;
        var previousContent = RollbackLastApplyButton.Content;
        RollbackLastApplyButton.IsEnabled = false;
        RollbackLastApplyButton.Content = "正在核对…";
        try
        {
            await EnsureStoresAsync();

            var preflight = await RollbackService.PrepareAsync();
            if (!preflight.CanRollback)
            {
                await ShowRollbackDialogAsync(
                    "现在还不能撤销",
                    preflight.Headline,
                    preflight.Detail);
                ShowInfo("现在还不能撤销", preflight.Detail, InfoBarSeverity.Warning);
                return;
            }

            if (!await ConfirmRollbackAsync(preflight))
                return;

            RollbackLastApplyButton.Content = "正在撤销…";
            var result = await RollbackService.RollbackAsync();

            var lines = new List<string> { result.Detail };
            if (result.Issues.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(result.Issues.Take(5));
            }

            await ShowRollbackDialogAsync("撤销上次整理", result.Headline, string.Join("\n", lines));

            if (result.IsSuccess)
            {
                ShowInfo("已撤销上次整理", preflight.Detail, InfoBarSeverity.Success);
                // 桌面已经不是刚才预览时的样子：丢掉冻结预览证据，重新走一遍「预览 → 应用」。
                InvalidateDirectApplyEvidence("已撤销上次整理 · 请重新预览当前桌面");
            }
            else
            {
                ShowError(result.Headline, string.Join(" ", lines));
            }
        }
        catch (Exception ex)
        {
            await ShowRollbackDialogAsync("撤销没有完成", "撤销出错", $"{ex.GetType().Name}: {ex.Message}");
            ShowError("撤销没有完成", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _rollbackInProgress = false;
            RollbackLastApplyButton.IsEnabled = true;
            RollbackLastApplyButton.Content = previousContent;
        }
    }

    private async Task<bool> ConfirmRollbackAsync(DesktopLayoutRollbackPreflightResult preflight)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销上次整理？",
            Content = new TextBlock
            {
                Text = preflight.Detail
                    + "\n\n撤销只按那次整理之前的安全检查点把项目放回原位，不会凭推断摆位置。"
                    + "执行期间请不要手工挪动桌面图标。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowRollbackDialogAsync(string title, string headline, string body)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer { Content = content, MaxHeight = 420 },
            CloseButtonText = "关闭",
        };

        await dialog.ShowAsync();
    }
}
