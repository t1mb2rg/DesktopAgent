using DesktopAgent.Application.DesktopNotifications;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DesktopAgent.WindowsInterop.DesktopNotifications;

public sealed record WindowsAppNotificationRegistrationResult(
    bool IsRegistered,
    string? Setting,
    string? Failure);

/// <summary>
/// Windows App SDK 本地 App Notification surface。
/// 只实现 M3-04A notification sink contract，不持有 Candidate / Writer。
/// </summary>
public sealed class WindowsAppNotificationSink : IDesktopSuggestionNotificationSink, IDisposable
{
    private const string SuggestionGroup = "desktop-suggestion";

    private readonly object _gate = new();

    private AppNotificationManager? _manager;
    private bool _registered;
    private int _disposeStarted;
    private WindowsAppNotificationRegistrationResult _registration =
        new(false, null, "Not registered yet.");

    public event Action<DesktopSuggestionNotificationInteraction>? InteractionReceived;

    public WindowsAppNotificationRegistrationResult Registration
    {
        get
        {
            lock (_gate)
                return _registration;
        }
    }

    /// <summary>
    /// 必须在 Windows App SDK runtime/deployment 已显式初始化后调用。
    /// 按照 Windows App SDK 要求，NotificationInvoked handler 先订阅，再调用 Register。
    /// AppNotificationManager.Default 也延迟到这里获取，使 runtime bootstrap 失败时 DI 仍可降级启动。
    /// </summary>
    public WindowsAppNotificationRegistrationResult Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);

            if (_registered)
                return _registration;

            AppNotificationManager? manager = null;
            var handlerAttached = false;
            try
            {
                manager = AppNotificationManager.Default;
                manager.NotificationInvoked += OnNotificationInvoked;
                handlerAttached = true;
                manager.Register();

                _manager = manager;
                _registered = true;
                _registration = new WindowsAppNotificationRegistrationResult(
                    true,
                    manager.Setting.ToString(),
                    null);
            }
            catch (Exception ex)
            {
                if (handlerAttached && manager is not null)
                {
                    try
                    {
                        manager.NotificationInvoked -= OnNotificationInvoked;
                    }
                    catch
                    {
                    }
                }

                _manager = null;
                _registered = false;
                _registration = new WindowsAppNotificationRegistrationResult(
                    false,
                    null,
                    ex.ToString());
            }

            return _registration;
        }
    }

    public Task PublishAsync(
        DesktopSuggestionNotificationPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ct.ThrowIfCancellationRequested();
        var manager = GetRegisteredManager();

        var suggestionId = payload.SuggestionId.ToString("D");
        var builder = new AppNotificationBuilder()
            // Clicking the notification body behaves like OpenSuggestion.
            .AddArgument(
                WindowsAppNotificationInteractionParser.ActionKey,
                WindowsAppNotificationInteractionParser.OpenAction)
            .AddArgument(
                WindowsAppNotificationInteractionParser.SuggestionIdKey,
                suggestionId)
            .AddText("DesktopAgent 有新的桌面整理建议")
            .AddText(
                $"预计移动 {payload.MoveRequiredCount} 个项目，目标显示器 {payload.TargetDeviceName}。")
            .AddButton(
                new AppNotificationButton("查看建议")
                    .AddArgument(
                        WindowsAppNotificationInteractionParser.ActionKey,
                        WindowsAppNotificationInteractionParser.OpenAction)
                    .AddArgument(
                        WindowsAppNotificationInteractionParser.SuggestionIdKey,
                        suggestionId))
            .AddButton(
                new AppNotificationButton("忽略")
                    .AddArgument(
                        WindowsAppNotificationInteractionParser.ActionKey,
                        WindowsAppNotificationInteractionParser.IgnoreAction)
                    .AddArgument(
                        WindowsAppNotificationInteractionParser.SuggestionIdKey,
                        suggestionId))
            .SetTag(WindowsAppNotificationInteractionParser.CreateTag(payload.SuggestionId))
            .SetGroup(SuggestionGroup);

        if (payload.UsesTemporaryStaging)
            builder.AddText("该建议包含临时 staging 步骤，执行前仍会重新走 M2 安全链。");

        var notification = builder.BuildNotification();
        manager.Show(notification);
        return Task.CompletedTask;
    }

    public async Task WithdrawAsync(
        Guid suggestionId,
        CancellationToken ct = default)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("suggestionId 不能为空。", nameof(suggestionId));
        ct.ThrowIfCancellationRequested();
        var manager = GetRegisteredManager();

        await manager.RemoveByTagAsync(
            WindowsAppNotificationInteractionParser.CreateTag(suggestionId));
        ct.ThrowIfCancellationRequested();
    }

    public void Stop()
    {
        lock (_gate)
        {
            var manager = _manager;
            if (!_registered || manager is null)
                return;

            _registered = false;
            _manager = null;

            try
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
            }
            catch
            {
            }

            try
            {
                manager.Unregister();
                _registration = new WindowsAppNotificationRegistrationResult(
                    false,
                    null,
                    "Unregistered.");
            }
            catch (Exception ex)
            {
                // Shutdown cleanup must not kill DesktopAgent. Registration is already
                // considered closed locally and a future process will register fresh.
                _registration = new WindowsAppNotificationRegistrationResult(
                    false,
                    null,
                    ex.ToString());
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        Stop();
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        _ = sender;

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in args.Arguments)
            arguments[pair.Key] = pair.Value;

        if (!WindowsAppNotificationInteractionParser.TryParse(arguments, out var interaction)
            || interaction is null)
        {
            return;
        }

        PublishSafely(InteractionReceived, interaction);
    }

    private AppNotificationManager GetRegisteredManager()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);

            if (!_registered || _manager is null)
            {
                throw new InvalidOperationException(
                    "Windows App Notification 尚未成功注册。请先初始化 Windows App SDK runtime，调用 Start，并检查 Registration.Failure。");
            }

            return _manager;
        }
    }

    private static void PublishSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
            return;

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // Notification activation observer 不得反向破坏平台回调线程。
            }
        }
    }
}
