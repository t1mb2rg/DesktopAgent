using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow : ITrayCommandTarget
{
    void ITrayCommandTarget.HandleTrayCommand(TrayIconCommand command)
    {
        switch (command)
        {
            case TrayIconCommand.Open:
                ShowFromBackground();
                break;

            case TrayIconCommand.Layouts:
                OpenSectionFromTray("layouts");
                break;

            case TrayIconCommand.Pending:
                OpenSectionFromTray("pending");
                break;

            case TrayIconCommand.Settings:
                OpenSectionFromTray("settings");
                break;

            case TrayIconCommand.Exit:
                ExitApplication();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "未知 tray command。");
        }
    }

    private void OpenSectionFromTray(string tag)
    {
        ShowFromBackground();

        var navigationItem = FindNavigationItem(tag);
        var wasAlreadySelected =
            navigationItem is not null && ReferenceEquals(ShellNavigation.SelectedItem, navigationItem);

        if (navigationItem is not null)
            ShellNavigation.SelectedItem = navigationItem;

        // 即使 NavigationView 因为 SelectedItem 未变化而不触发 SelectionChanged，
        // 托盘命令也必须保证目标页可见。
        ShowSection(tag);

        if (wasAlreadySelected && string.Equals(tag, "pending", StringComparison.Ordinal))
            _ = RefreshPendingAsync();
    }

    private NavigationViewItem? FindNavigationItem(string tag)
    {
        foreach (var item in ShellNavigation.MenuItems)
        {
            if (item is NavigationViewItem navigationItem &&
                string.Equals(navigationItem.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                return navigationItem;
            }
        }

        foreach (var item in ShellNavigation.FooterMenuItems)
        {
            if (item is NavigationViewItem navigationItem &&
                string.Equals(navigationItem.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                return navigationItem;
            }
        }

        return null;
    }
}
