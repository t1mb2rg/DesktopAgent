using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopAgent.Poc;

public partial class MainWindow
{
    private bool _diagnosticsWorkbenchIdentityApplied;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_diagnosticsWorkbenchIdentityApplied)
            return;

        _diagnosticsWorkbenchIdentityApplied = true;
        ApplyDiagnosticsWorkbenchIdentity();

        if (System.Windows.Application.Current is App app)
            app.ScheduleDiagnosticsWorkbenchLifecycle(this);
    }

    private void ApplyDiagnosticsWorkbenchIdentity()
    {
        Title = "DesktopAgent Diagnostics Workbench";

        var header = FindWorkbenchHeader(this);
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
