# M3-04C：Windows App Notification Surface

## 目标

把 M3-04A/04B 已验证的 notification policy / scheduler 接到 Windows 真实本地通知 surface，同时保持平台层只拿到瘦 `DesktopSuggestionNotificationPayload`：

```text
Suggestion lifecycle
→ 04A policy / dispatcher
→ 04B deferred scheduler
→ diagnostics mirror sink
→ Windows App SDK AppNotificationManager
→ Windows Notification Center
→ Open / Ignore activation
→ STOP
```

本阶段仍然没有 Candidate execution、Writer 或 ExecutionCoordinator。

## 为什么使用 Windows App Notification

当前 Windows App SDK 对 WPF / WinForms / 未打包 Win32 桌面应用提供 `AppNotificationManager`。

DesktopAgent PoC 当前是未打包 WPF，因此 04C 采用 Windows App SDK 本地 App Notification，而不是旧 `ToastNotificationManager` 或仅用 balloon tip。

Tray 仍然有价值，但职责不同：

- App Notification：主动提醒“有新的 Suggestion”；
- Tray：以后提供常驻入口、状态、暂停/恢复等长期控制。

因此 Tray 留到后续步骤，不和 04C 的 notification surface 混在同一 PR。

## 项目配置

WindowsInterop / PoC / Tests 的 Windows TFM 对齐为：

```xml
net10.0-windows10.0.19041.0
```

WindowsInterop 引入：

```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.3.1" />
```

PoC 仍为 unpackaged WPF，但最终 04C 不使用 executable module auto-initializer。

PoC 显式关闭：

```xml
<WindowsAppSdkBootstrapInitialize>false</WindowsAppSdkBootstrapInitialize>
<WindowsAppSdkDeploymentManagerInitialize>false</WindowsAppSdkDeploymentManagerInitialize>
<WindowsAppSdkUndockedRegFreeWinRTInitialize>false</WindowsAppSdkUndockedRegFreeWinRTInitialize>
```

Tests 同样显式关闭这些 auto-initializer，确保 xUnit 仅加载 PoC / WindowsInterop assembly 时不会偷偷初始化 Windows App SDK process state。

## 为什么改为显式 bootstrap

04C 第一版曾在 PoC 中使用：

```xml
<WindowsPackageType>None</WindowsPackageType>
```

它会为 executable 注入 Windows App SDK bootstrap auto-initializer。

CI 第一轮 Restore / Build 均通过，但 Test 相比上一层约 31 秒的正常耗时出现明显长期停滞。由于 tests 会引用并加载 PoC assembly，而 WinAppSDK executable module initializer 会在 assembly load 时执行，我们将其视为高概率的 test-host platform-initialization 污染。

最终设计改为：

```text
load DesktopAgent.Poc.dll in xUnit
→ NO WinAppSDK runtime bootstrap

real DesktopAgent.exe startup
→ explicit Bootstrap.Initialize
→ explicit DeploymentManager.Initialize
→ then AppNotificationManager
```

这也让 runtime / Singleton 缺失变成可观察、可降级的启动结果，而不是模块初始化阶段的隐式副作用。

## Runtime / Singleton 初始化

新增 `WindowsAppSdkRuntimeBootstrapper`。

当前 Microsoft.WindowsAppSDK 为 2.3.x，对应：

```text
Bootstrap.Initialize(0x0002_0003)
```

随后显式：

```text
DeploymentManager.Initialize(
    OnErrorShowUI=false,
    ForceDeployment=false)
```

`AppNotificationManager` 依赖 Singleton package，因此只 bootstrap Framework package 不足以作为最终运行前提。

成功条件：

```text
Bootstrap initialized
AND DeploymentStatus == Ok
```

失败时：

- 尝试 `Bootstrap.Shutdown()`；
- 返回 `WindowsAppSdkRuntimeInitializationResult`；
- DesktopAgent 主程序继续运行；
- Suggestion / Diff 不受影响；
- notification composition 退回 diagnostics-only 内存 surface。

退出时先 Dispose Host / notification sink，再 Shutdown Bootstrap runtime。

## 注册生命周期

runtime / deployment 成功以后，`WindowsAppNotificationSink.Start()`：

```text
resolve AppNotificationManager.Default
→ subscribe NotificationInvoked
→ AppNotificationManager.Register()
→ record notification Setting
```

`AppNotificationManager.Default` 被延迟到 `Start()` 获取，constructor 本身不触碰 Windows App SDK runtime。因此即使 runtime 初始化失败，DI 仍能构造其余 DesktopAgent 服务。

必须先订阅 activation handler，再 Register。

注册失败不会杀死 DesktopAgent：

- Suggestion / Diff 继续工作；
- Notification sink 保持未注册；
- 后续 Publish / Withdraw fail closed；
- Debug PoC 会显示一次 registration warning；
- dispatcher 不会把失败的平台操作 acknowledge 成成功。

M2 Acceptance Fault Injection 启用时，04C 连 runtime bootstrap 都不执行，避免 DDLM / Singleton / COM activation / callback 干扰 crash-window 验收。

## Publish

每条 Suggestion 通知包含：

- 标题：DesktopAgent 有新的桌面整理建议；
- 预计移动数量；
- 目标显示器；
- 若含 staging，再增加一行提示；
- “查看建议”按钮；
- “忽略”按钮。

点击通知主体等价于 OpenSuggestion。

所有 activation 都携带：

```text
action
suggestionId
```

SuggestionId 是 interaction 的 stale-safety token，不是执行授权。

## Activation parser

`WindowsAppNotificationInteractionParser` 是纯逻辑边界：

- 必须有合法非空 SuggestionId；
- body click 缺 action 时默认 Open；
- 只接受 Open / Ignore；
- 未知 action fail closed。

Windows App SDK activation 回调只负责把 WinRT 参数复制为普通字典并交给 parser。

平台回调线程不直接碰 WPF。

## Tag / Withdraw

每条 Suggestion 使用确定性 tag：

```text
da-<SuggestionId N format>
```

例如：

```text
da-0123456789abcdef0123456789abcdef
```

04A 的 Withdraw 映射到 `RemoveByTagAsync`，从 Windows Notification Center 撤掉对应 Suggestion。

所有 Suggestion 使用统一 group：

```text
desktop-suggestion
```

## Diagnostics mirror

04B 的 `M3DiagnosticsNotificationSink` 在 04C 变成 wrapper：

```text
04A sink contract
→ M3DiagnosticsNotificationSink
→ WindowsAppNotificationSink
```

顺序严格为：

```text
platform Publish / Withdraw succeeds
→ diagnostics mirror updates
```

如果真实 Windows API 抛异常，镜像保持旧状态，不会假装通知已经发布/撤回。

真实 Windows activation 也会经 wrapper 原样转发到 04B interaction bridge。

如果 runtime bootstrap 本身失败，则 wrapper 不连接 real sink，退回纯内存 diagnostics surface。

PoC 的“模拟打开 / 模拟忽略”仍保留，用于主动制造 stale interaction 场景。

## Stale-safe Ignore 保持不变

Windows 按钮返回的 SuggestionId 最终仍进入：

```text
DismissCurrent(expectedSuggestionId)
```

如果 Notification Center 中残留的是旧 S1，而当前 lifecycle 已经是 S2：

```text
Ignore(S1)
→ Dismissed=false
→ S2 不受影响
→ scheduler 对当前 S2 重新 reconcile
```

平台接入没有绕过 04B 已验证的 stale safety。

## 生命周期 / 退出

正常退出：

```text
Host.Dispose
→ WindowsAppNotificationSink.Dispose
→ unsubscribe NotificationInvoked
→ AppNotificationManager.Unregister
→ WindowsAppSdkRuntimeBootstrapper.Dispose
→ Bootstrap.Shutdown
```

shutdown cleanup 失败不会反向导致 DesktopAgent 退出异常。

## 自动测试

CI 不依赖真实 Notification Center 弹窗，也不初始化 WinAppSDK runtime。

覆盖：

- activation Open / Ignore 解析；
- body click 默认 Open；
- invalid Guid / unknown action fail closed；
- deterministic Suggestion tag；
- diagnostics mirror 只有 inner Publish 成功后才推进；
- inner Withdraw 失败时 mirror 不提前清除；
- inner activation 原样穿透 mirror。

真实 Bootstrap / DeploymentManager / Register / Show / button activation / RemoveByTag 需要 Windows 实机验收。

## 部署注意

当前 PoC 是 unpackaged + Windows App SDK frame-dependent 路径。

后续发布安装器时必须把 Windows App SDK Runtime 以及 AppNotification 所需 Singleton package 作为明确依赖处理，不能假设目标机器已经安装对应 runtime。

这属于部署/安装阶段责任，不在 04C 中偷偷改成 MSIX。

另外，Windows App Notification 不支持 elevated app。当前 DesktopAgent manifest 没有管理员执行级别要求，应继续避免为了普通桌面整理功能把主进程整体提权。

## 不在范围

- tray / NotifyIcon；
- 开机启动；
- notification persistence / startup replay；
- package/MSIX；
- user-confirmed execution handoff；
- unattended Writer。

## 下一步

04C CI + Windows 实机通知验收通过后，进入 M3-04D tray foundation：

```text
tray icon
→ 打开主窗口 / diagnostics
→ 显示 monitoring / suggestion / notification 状态
→ 暂停 / 恢复后台监控
```

Tray 同样不得获得 M2 Candidate / Writer。
