# M3-04D：Tray Foundation

## 目标

让 DesktopAgent 从“打开窗口时才像一个应用”迈到真正的后台常驻形态，同时保持 M3-A 只读门禁：

```text
Windows tray
→ 打开主窗口 / M3 Diagnostics
→ 显示 monitoring / Suggestion / notification 状态
→ 暂停 / 恢复只读 monitoring
→ 显式 Exit
→ STOP
```

Tray 不获得 M2 Candidate、Writer 或 ExecutionCoordinator。

## Tray 与 App Notification 的分工

04C Windows App Notification 负责主动提醒：

```text
“有一条新的 Suggestion”
```

04D tray 负责长期入口与状态：

- DesktopAgent 是否还在运行；
- monitoring 是否运行；
- 当前 Suggestion 是 None / Active / Suspended；
- 当前是否存在 Suggestion notification；
- 打开主窗口；
- 打开 M3 Diagnostics；
- 暂停 / 恢复 monitoring；
- 显式退出程序。

不使用 balloon tip 替代 04C 的 App Notification。Tray balloon 只保留给 tray 自身操作失败的本地反馈。

## WPF 生命周期

`App.xaml` 改为：

```xml
ShutdownMode="OnExplicitShutdown"
```

因此所有窗口都隐藏后，DesktopAgent 进程仍可由 tray 持续存在。

真正结束应用只有：

```text
Tray → 退出 DesktopAgent
→ MainWindow 原 M2 Closing 安全检查
→ allowed
→ Application.Shutdown()
```

## Close-to-Tray

### MainWindow

MainWindow 已有自己的 M2 `OnClosing`：

- 正在执行操作时阻止关闭；
- 布局恢复需要人工处理时要求确认；
- 活跃移动恢复会话未收口时要求确认。

04D 不替换、不绕过这套逻辑。

Tray controller 在 MainWindow 构造完成以后追加自己的 Closing handler，因此顺序为：

```text
MainWindow existing safety OnClosing
→ if e.Cancel=true: tray does nothing
→ if safety allows close: tray sets e.Cancel=true and Hide()
```

所以普通 X 只隐藏到托盘，但 M2 安全检查仍然先执行。

### M3DiagnosticsWindow

普通 X：

```text
Closing
→ tray sets e.Cancel=true
→ Hide()
```

不会触发 03D 的 `Closed` cleanup，因此隐藏 Diagnostics 不再自动 StopMonitoring。

真正 Tray Exit 时 `_exitRequested=true`，Closing 不再拦截，窗口正常关闭，03D `Closed` cleanup 仍作为最终资源回收兜底。

## Tray Exit

Tray Exit：

1. 标记 `_exitRequested`；
2. 若 M3 monitoring 正在运行，先保守 Stop；
3. 恢复/激活 MainWindow；
4. 调用 `MainWindow.Close()`，让原 M2 Closing 安全门真正执行；
5. 如果 MainWindow 仍 `IsLoaded`，说明安全门取消了关闭：
   - 取消 exit request；
   - 应用继续运行；
   - 主窗口保持可见，方便人工处理；
6. 只有 MainWindow 真正关闭后才隐藏 tray icon 并 `Application.Shutdown()`。

因此 tray 不提供绕过 M2 恢复锁的“强退”路径。

## Monitoring 控制

Tray 菜单只调用现有：

```text
M3SuggestionMonitoringCoordinator.StartMonitoringAsync()
M3SuggestionMonitoringCoordinator.StopMonitoringAsync()
```

不会直接操作 watcher / pump / Shell Reader。

04D 不自动启动 monitoring。用户仍可从 Diagnostics 或 tray 显式恢复 monitoring。

## 状态刷新

Tray 每秒只读取进程内状态：

```text
IsMonitoring
SuggestionSnapshot
Notification SurfaceSnapshot
```

不进行 Shell 读取，不制造新的 Observation，不影响 debounce / periodic cadence。

这样即使用户从 Diagnostics 里启动/停止 monitoring，tray 状态也能在 1 秒内同步，而不需要把 03D 已验证的 monitoring coordinator 为 UI 再重构一遍。

## UI 技术选择

PoC 同时启用：

```xml
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>
```

Tray 使用 `System.Windows.Forms.NotifyIcon` + `ContextMenuStrip`。

04D 暂用 `SystemIcons.Application` 作为占位图标，只验证生命周期和交互；自定义 DesktopAgent 图标留到正式 UI/品牌资产阶段。

## Acceptance 隔离

M2 Acceptance Fault Injection 启用时：

- 不 bootstrap Windows App SDK；
- 不注册 App Notification；
- 不启动 tray；
- 不自动打开 M3 Diagnostics。

继续保持 M2 crash-window / SoftError 验收环境单一。

## 结构护栏

`M3TrayController` 构造依赖只允许：

- MainWindow；
- M3DiagnosticsWindow；
- M3SuggestionMonitoringCoordinator；
- M3SuggestionNotificationCoordinator。

没有直接 `IDesktopIconPositionWriter` 依赖。

打开 MainWindow 只是把已有人工 UI surface 显示出来，不等于 tray 获得 Writer 能力。

## 自动测试 / CI 范围

自动测试继续做结构边界检查：Tray controller 不直接依赖 Writer。

NotifyIcon 的真实 notification-area 可见性、窗口 Hide/Show、右键菜单和 Exit 的 WPF/WinForms 联动需要 Windows 实机验收。

## 不在范围

- 自动启动 monitoring；
- 开机启动；
- 自定义 tray icon；
- tray 持久化设置；
- 自动模式 / unattended Writer；
- user-confirmed execution handoff。

## Windows 实机验收建议

1. 启动普通 Debug PoC，确认 tray icon 出现；
2. 关闭 MainWindow，确认进程仍在、tray 仍在；
3. 从 tray 打开 MainWindow；
4. 关闭 Diagnostics，确认 monitoring 不因窗口隐藏而停止；
5. 从 tray 打开 Diagnostics；
6. tray 启动 monitoring，观察菜单状态变为运行中；
7. tray 暂停 monitoring，观察状态回到已暂停；
8. 触发 Active / Suspended Suggestion，确认 tray 状态跟随；
9. 使用 Tray Exit，确认无 M2 阻断状态时进程退出；
10. 有 M2 正在执行 / 人工恢复阻断状态时，确认原安全关闭逻辑仍优先，tray 不强退。

## 下一步

04D 通过 CI 与 Windows 实机验收后，可以进入 M3-04E / 05A：

- automation history / diagnostics 持久化；
- pause / cooldown / fuse 状态模型；
- 或在 M2 双屏实机门通过后再讨论 user-confirmed execution handoff。

在 M2 双屏验收未完成前，仍不进入 unattended real Writer。
