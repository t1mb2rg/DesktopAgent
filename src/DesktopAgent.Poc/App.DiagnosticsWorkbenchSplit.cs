using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopAgent.Poc.Acceptance;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopAgent.Poc;

public partial class App
{
    private bool _diagnosticsWorkbenchSplitScheduled;
    private bool _diagnosticsWorkbenchIdentityApplied;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        if (_diagnosticsWorkbenchSplitScheduled)
            return;

        _diagnosticsWorkbenchSplitScheduled = true;

        // mainWindow.Show() 可能在 App.OnStartup 尚未执行完时就触发 Activated。
        // 排到 ApplicationIdle，确保旧 M3 composition 已完成，再决定是否拆除 resident tray。
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(ApplyDiagnosticsWorkbenchSplit));
    }

    private void ApplyDiagnosticsWorkbenchSplit()
    {
        if (_diagnosticsWorkbenchIdentityApplied ||
            MainWindow is not DesktopAgent.Poc.MainWindow mainWindow ||
            _host is null)
        {
            return;
        }

        _diagnosticsWorkbenchIdentityApplied = true;
        ApplyDiagnosticsWorkbenchIdentity(mainWindow);

        var runtimeProfile = DiagnosticsWorkbenchRuntimeProfile.For(IsResidentAcceptanceArmed());
        if (!runtimeProfile.KeepResidentTray)
        {
            var tray = _host.Services.GetRequiredService<M3TrayController>();
            if (tray.IsStarted)
                tray.Dispose();
        }

        if (runtimeProfile.ShutdownOnMainWindowClose)
            mainWindow.Closed += (_, _) => Shutdown();
    }

    private static bool IsResidentAcceptanceArmed()
    {
#if DEBUG
        // OnStartup 已先验证 acceptance 环境变量；这里仅复用同一配置判断是否必须
        // 保留旧 M3 resident tray。若环境在运行中被外部改坏，宁可按普通 diagnostics
        // fail-closed 为非 resident，而不是意外保留 unattended 控制面。
        try
        {
            return M3AutomaticAcceptanceOptions.FromEnvironment().IsEnabled;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

    private static void ApplyDiagnosticsWorkbenchIdentity(DesktopAgent.Poc.MainWindow mainWindow)
    {
        mainWindow.Title = "DesktopAgent Diagnostics Workbench";

        var header = FindWorkbenchHeader(mainWindow);
        if (header is null)
            return;

        header.Text = "DesktopAgent Diagnostics Workbench";

        if (header.Parent is not StackPanel headerPanel ||
            headerPanel.Children.OfType<Border>().Any(border =>
                string.Equals(border.Tag as string, "DiagnosticsWorkbenchIdentity", StringComparison.Ordinal)))
        {
            return;
        }

        var badge = new Border
        {
            Tag = "DiagnosticsWorkbenchIdentity",
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xA1, 0x00)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "工程诊断工作台 · 非正式产品 UI · Writer / Recovery 操作继续受既有安全 Gate 约束",
                Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0x32, 0x00)),
                FontSize = 11,
            },
        };

        headerPanel.Children.Add(badge);
    }

    private static TextBlock? FindWorkbenchHeader(DependencyObject root)
    {
        if (root is TextBlock textBlock &&
            textBlock.Text.StartsWith("DesktopAgent PoC", StringComparison.Ordinal))
        {
            return textBlock;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject)
                continue;

            var match = FindWorkbenchHeader(dependencyObject);
            if (match is not null)
                return match;
        }

        return null;
    }
}
