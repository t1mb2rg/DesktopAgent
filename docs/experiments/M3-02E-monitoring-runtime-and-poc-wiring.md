# M3-02E：Monitoring Runtime 与 PoC 生命周期接线

## 目标

把 M3-02A～02D 已完成的只读能力真正接入 PoC 常驻生命周期：

```text
Windows hint source
→ HintBatcher
→ BackgroundPump
→ single-flight BatchProcessor
→ stable Observation
→ baseline / State Diff
→ MonitoringRuntime
→ Diagnostics Window
→ STOP
```

仍然不接 Suggestion，也不接 unattended Writer。

## Application Runtime

新增 `DesktopObservationMonitoringRuntime`，负责：

- 启动 / 停止 `DesktopObservationBackgroundPump`；
- 启动 / 停止所有注册的 `IDesktopObservationHintSource`；
- 启动后发布一次 `M3MonitoringStartup` Manual hint 建立 fresh baseline；
- 为 diagnostics 暴露 `Status` / `LastRun` / `RunCompleted`；
- 把手动观察请求转换为 `M3DiagnosticsManual` hint；
- 提供当前会话 baseline reset。

Runtime 位于 Application，不依赖 WPF、WindowsInterop 或 Writer。

## Generic hint source lifecycle port

Application 新增 `IDesktopObservationHintSource`。

当前 `DesktopFileSystemObservationHintSource` 作为第一个实现，同时保留 M3-02D 原有公开 API。

后续 Display / Shell / periodic source 可直接以同一生命周期接口注册，不需要修改 Runtime。

## Monitoring 状态

状态：

- `Running`：至少一个真实 hint source 运行，且启动无已知降级；
- `Degraded`：部分 source 失败、source 带启动 issue，或没有真实 source 成功运行；
- `Stopped`：Runtime 已停止。

即使没有真实 hint source，Runtime 仍可通过 Manual hint 进行 fresh observation，因此 Degraded 不等于 Writer 或数据安全失败。

## Host 生命周期

PoC 新增薄 `M3ObservationMonitoringHostedService`：

```text
Host.StartAsync → Runtime.Start()
Host.StopAsync  → Runtime.StopAsync()
```

HostedService 不承载 debounce / observation / diff 逻辑，也不持有 Writer。

M2 Acceptance Fault Injection 启用时，不注册该 HostedService，避免 crash-window / SoftError 验收期间后台 Shell read 干扰实验环境。

## PoC DI 参数

当前仅用于 PoC：

- stability sample interval：500 ms；
- hint quiet period：500 ms；
- max batch delay：2 s。

这些值都不是 Core 冻结语义，后续可通过实机观测调整。

## Diagnostics Window

从本阶段开始，Diagnostics Window 不再直接调用 `DesktopObservationDiagnosticsRunner`。

“请求观察”按钮只发布 Manual hint：

```text
UI
→ Runtime.RequestManualObservation()
→ pump.TryPublishHint(Manual)
→ bounded debounce
→ single-flight pipeline
```

因此手动与自动观察共享同一个执行顺序，不会并发读取 Shell 或乱序推进 baseline。

窗口同时显示：

- Runtime Running / Degraded / Stopped；
- hint source endpoint / issue；
- trigger batch hint count / kinds / sources；
- run timing；
- stable observation / diff / unavailable / unstable diagnostics。

Runtime 回调不假设 WPF Dispatcher，窗口负责 marshal 回 UI thread。

## 当前真实覆盖

自动触发来源目前只有 Desktop 文件系统：

- 用户 Desktop 顶层 create / delete / rename；
- Public Desktop 顶层 create / delete / rename；
- watcher infrastructure error。

纯图标位置拖动不会可靠触发 FileSystemWatcher，这一缺口仍然明确保留，后续由 Shell / periodic / display sources 补齐。

## Read-only boundary

结构测试冻结：

- `DesktopObservationMonitoringRuntime` 不暴露 `IDesktopIconPositionWriter`；
- `M3DiagnosticsWindow` 只依赖 MonitoringRuntime；
- `M3ObservationMonitoringHostedService` 只依赖 MonitoringRuntime。

M3-A unattended Writer gate 保持关闭。

## 自动测试

新增覆盖：

- clean source → Running；
- source issue → Degraded；
- no active source → Degraded 但 startup/manual observation 仍工作；
- source Start exception → fail-soft status；
- startup hint 建立 baseline；
- manual hint 继续同一 baseline 并产生 NoChanges；
- Start 幂等；
- Stop 后 Runtime 不允许重启；
- Start 前 manual request 返回 false；
- Runtime / Window / HostedService 不暴露 Writer dependency。

## 后续

M3-02F 建议优先补 periodic fallback 或 display topology hint source，再决定 Shell change notification 的具体实现。

原因：FileSystemWatcher 已覆盖文件创建/删除/重命名，但纯位置变化仍无 hint；周期 fresh observation 是最简单可靠的兜底，可防止某类 Windows 事件永久漏检。
