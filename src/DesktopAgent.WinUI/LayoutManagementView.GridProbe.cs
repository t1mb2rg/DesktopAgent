using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DesktopAgent.WinUI;

/// <summary>
/// 产品侧的校准入口：把"这台机器上图标会落在哪"从推断变成实测。
///
/// 全部显示器的实测网格是产品入口的前置条件，不等于无人值守执行授权。
/// </summary>
public sealed partial class LayoutManagementView
{
    private bool _gridProbeRunning;
    private CancellationTokenSource? _gridProbeCancellation;

    private async void RunGridProbe_Click(object sender, RoutedEventArgs e)
    {
        if (_gridProbeRunning)
        {
            _gridProbeCancellation?.Cancel();
            GridPreparationStatusText.Text = "校准：正在停止扫描，完成恢复与临时文件清理后结束…";
            return;
        }

        if (_directApplyInProgress)
        {
            ShowInfo("暂时无法校准", "桌面正在应用布局。等它结束再校准，避免两种写操作互相干扰。", InfoBarSeverity.Warning);
            return;
        }

        try { await RunRequiredCalibrationAsync(); }
        catch (Exception ex) { ShowError("校准未完成", ex.Message); }
    }

    private async Task<bool> ConfirmGridProbeAsync()
    {
        var consent = new CheckBox
        {
            Content = "我明白：校准期间不要拖动图标、增删桌面文件、切换自动排列/网格，也不要改变显示器拓扑。",
            IsChecked = false,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "校准会临时创建专用测试快捷方式，并真实移动它们来测出这块屏的网格（间距与起始相位）。"
                 + "结束后会把测试图标放回、删除临时文件，并核验你的既有图标没有被留在别处。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "结果按显示器逐屏记录：只有拿到合格实测样本的屏会被写入网格事实；测不到的屏如实记为未实测，不会用推断顶替。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(consent);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "校准桌面图标网格？",
            Content = panel,
            PrimaryButtonText = "开始校准",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        consent.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        consent.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RunGridProbeAsync(Action<string>? reportProgress = null)
    {
        _gridProbeRunning = true;
        _gridProbeCancellation = new CancellationTokenSource();
        GridProbeButton.Content = "校准中…（点此停止）";
        LayoutStatusBar.IsOpen = false;

        var logDirectory = ExplorerGridProbeSession.LogDirectoryFor(DesktopAgentDataPaths.DefaultDatabasePath);
        var progress = new Progress<string>(message =>
        {
            GridPreparationStatusText.Text = $"校准中：{message}";
            reportProgress?.Invoke(GridPreparationStatusText.Text);
        });

        try
        {
            await ProductGridCalibration.InvalidateAsync();
            var session = new ExplorerGridProbeSession(
                ExplorerGridPhysicalProbeRunnerFactory.Create(_observationSource, _staThreadExecutor),
                new ExplorerGridProbeFactRecorder(
                    new SqliteExplorerGridMeasurementStore(DesktopAgentDataPaths.DefaultDatabasePath)),
                logDirectory);

            var result = await session.RunAsync(progress, _gridProbeCancellation.Token);
            await PresentGridProbeResultAsync(result);
        }
        catch (OperationCanceledException)
        {
            ShowInfo("校准已停止", "扫描已停止。请在报告里确认恢复、临时文件删除与既有图标核验结果。", InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            var text = $"校准出现逃逸异常：{exception.GetType().Name}: {exception.Message}";
            ExplorerGridProbeSession.PersistReport(text, logDirectory);
            ShowError("校准失败", text);
            throw;
        }
        finally
        {
            _gridProbeCancellation?.Dispose();
            _gridProbeCancellation = null;
            _gridProbeRunning = false;
            GridProbeButton.Content = "校准桌面网格…";
        }
    }

    private async Task PresentGridProbeResultAsync(ExplorerGridProbeSessionResult result)
    {
        var total = result.Report.Displays.Count;
        var headline = result.Status switch
        {
            ExplorerGridProbeRunStatus.Passed => "每一块显示器都拿到了合格实测样本。",
            ExplorerGridProbeRunStatus.Partial => "部分显示器拿到了合格实测样本，另有显示器明确测不到。",
            ExplorerGridProbeRunStatus.Cancelled => "扫描被停止；恢复与清理结果见报告。",
            ExplorerGridProbeRunStatus.Blocked => "没有形成结论：安全空位或主动样本不足。",
            _ => "扫描未通过：报告里记录了首个异常与恢复结果。",
        };
        var factLine = result.Recorded.Facts.Count > 0
            ? $"已写入 {result.Recorded.Facts.Count} 条实测网格事实。"
            : "未写入网格事实（原因见报告）——完成全部显示器校准后才能进入产品。";
        GridPreparationStatusText.Text = $"校准结果：实测 {result.MeasuredDisplayCount}/{total} 块显示器；{factLine}";

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{headline}\n实测 {result.MeasuredDisplayCount}/{total} 块显示器。{factLine}"
                 + (result.ReportPath is { Length: > 0 } path
                     ? $"\n报告已保存：{path}"
                     : "\n报告未落盘（界面里仍有完整内容）。"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBox
        {
            Text = result.ReportText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            Height = 340,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "校准结果",
            Content = panel,
            PrimaryButtonText = "关闭",
            SecondaryButtonText = "复制报告",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(result.ReportText);
                Clipboard.SetContent(package);
                GridPreparationStatusText.Text = "报告已复制到剪贴板。";
            }
            catch (Exception exception)
            {
                GridPreparationStatusText.Text = $"复制报告失败：{exception.GetType().Name}: {exception.Message}";
            }

            args.Cancel = true;
        };

        await dialog.ShowAsync();
    }
}
