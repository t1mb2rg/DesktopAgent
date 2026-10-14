using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.Productization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

/// <summary>
/// 站定授权的"看得见、收得回"入口。
///
/// 授权只能由用户当面确认过的那条路径签发（工作区应用对话框里的"记住这些显示器"）；
/// 收回走这里。收回是纯粹减小授权面的操作，因此不需要再确认一次，但它同样必须绑在
/// 当前显示设置上：为一段已经不成立的环境写撤销没有意义，所以读不到拓扑就不写。
///
/// 收回不移动任何桌面项目，也不中断正在进行的执行——它只改变"以后还会不会被自动执行"。
/// </summary>
public sealed partial class LayoutManagementView
{
    private async void GridDisplayAuthorizations_Click(object sender, RoutedEventArgs e)
    {
        if (_directApplyInProgress)
        {
            ShowInfo(
                "正在执行布局",
                "等这次执行彻底结束后再看已记住的显示器：执行期间需要读同一套桌面状态。",
                InfoBarSeverity.Warning);
            return;
        }

        var databasePath = DesktopAgentDataPaths.DefaultDatabasePath;
        var snapshot = await ReadDisplayAuthorizationsAsync(databasePath);
        if (snapshot is null)
            return;

        if (!snapshot.IsAvailable)
        {
            var unavailableReason = snapshot.Status switch
            {
                DesktopGridDisplayAuthorizationQueryStatus.ObservationUnavailable =>
                    "重新读取当前桌面失败，现在无法确定这套显示设置。",
                DesktopGridDisplayAuthorizationQueryStatus.TopologyUnavailable =>
                    "算不出当前显示拓扑指纹，授权没有可以依附的环境。",
                DesktopGridDisplayAuthorizationQueryStatus.StoreFailed =>
                    "授权记录读取失败。这不等于没有授权——只是现在读不出来。",
                _ => "站定授权当前不可读。",
            };

            ShowInfo(
                "读不到当前的站定授权",
                $"{unavailableReason}为了安全，这次不显示任何授权，也不接受收回。",
                InfoBarSeverity.Warning);
            return;
        }

        if (snapshot.Authorizations.Count == 0)
        {
            ShowInfo(
                "当前没有记住任何显示器",
                "在这套显示设置下 Tessel 还不能无人值守执行：每次都会先给你一份计划等你确认。"
                + "想让它自己动手，就在确认应用布局时勾选「记住这些显示器」。",
                InfoBarSeverity.Informational);
            return;
        }

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "这些显示器在当前显示设置下已经允许无人值守执行。取消勾选＝收回授权，"
                 + "之后 Tessel 会重新先给你计划、等你确认。收回授权不会移动任何桌面项目，"
                 + "也不会中断一次正在进行的执行。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        });

        var rows = new List<(string DeviceName, CheckBox Box)>();
        foreach (var authorization in snapshot.Authorizations)
        {
            var box = new CheckBox
            {
                Content = $"{authorization.DeviceName} · 记住于 "
                    + authorization.AuthorizedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                IsChecked = true,
            };
            rows.Add((authorization.DeviceName, box));
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "已记住的显示器",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var toRevoke = rows
            .Where(static row => row.Box.IsChecked != true)
            .Select(static row => row.DeviceName)
            .ToArray();

        if (toRevoke.Length == 0)
        {
            ShowInfo("没有改动", "这些显示器仍然被记住。", InfoBarSeverity.Informational);
            return;
        }

        await RevokeDisplayAuthorizationsAsync(databasePath, toRevoke);
    }

    private async Task<DesktopGridDisplayAuthorizationSnapshot?> ReadDisplayAuthorizationsAsync(
        string databasePath)
    {
        var service = new DesktopGridDisplayAuthorizationQueryService(
            _observationSource,
            new SqliteExplorerGridDisplayAuthorizationStore(databasePath));

        try
        {
            return await service.ReadAsync();
        }
        catch (Exception ex)
        {
            ShowError(
                "读不到已记住的显示器",
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task RevokeDisplayAuthorizationsAsync(
        string databasePath,
        IReadOnlyList<string> deviceNames)
    {
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            _observationSource,
            new SqliteExplorerGridDisplayAuthorizationStore(databasePath));

        DesktopGridDisplayAuthorizationRevokeOutcome outcome;
        try
        {
            outcome = await service.RevokeAsync(deviceNames);
        }
        catch (Exception ex)
        {
            ShowError(
                "收回授权时发生异常",
                $"{ex.GetType().Name}: {ex.Message}。这次没有写入任何收回记录。");
            return;
        }

        if (outcome.Succeeded)
        {
            ShowInfo(
                "已收回授权",
                $"{outcome.RevokedDeviceNames.Count} 块显示器不再允许无人值守执行，"
                + "下次会先给你一份计划等你确认。桌面项目没有被移动。",
                InfoBarSeverity.Success);
            return;
        }

        var reason = outcome.Status switch
        {
            DesktopGridDisplayAuthorizationRevokeStatus.NothingRequested =>
                "没有选择要收回的显示器。",
            DesktopGridDisplayAuthorizationRevokeStatus.ObservationUnavailable =>
                "重新读取当前桌面失败，无法确定这套显示设置，因此没有写入收回记录。",
            DesktopGridDisplayAuthorizationRevokeStatus.TopologyUnavailable =>
                "无法确定当前显示拓扑指纹，因此没有写入收回记录。",
            DesktopGridDisplayAuthorizationRevokeStatus.StoreFailed =>
                "收回记录写入失败。",
            _ => "收回没有生效。",
        };

        // 部分写入必须照实说：哪些已经收回、哪些还留着，用户要能分辨。
        var partial = outcome.RevokedDeviceNames.Count > 0
            ? $"已经收回的是：{string.Join("、", outcome.RevokedDeviceNames)}；"
              + "其余显示器仍然被记住。"
            : "没有任何显示器的授权被改变。";

        ShowInfo(
            "没能完整收回授权",
            $"{reason}{partial}请稍后重试。",
            InfoBarSeverity.Warning);
    }
}
