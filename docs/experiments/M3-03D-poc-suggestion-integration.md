# M3-03D：PoC Suggestion Integration

## 目标

把 M3-02G 的真实只读 monitoring、M3-03A Builder、03B Lifecycle、03C Pipeline 接到同一个 PoC diagnostics surface 中，让 Suggestion 在真实 Windows 桌面上可观察、可配置、可验证。

```text
FileSystem / Periodic hint
→ monitoring session
→ stable Observation / Diff
→ ordered Suggestion coordinator
→ explicit binding
→ 03C Suggestion pipeline
→ Active / Suspended / None
→ diagnostics UI
→ STOP
```

本阶段依然没有执行按钮、没有 Notification、没有 unattended Writer。

## M3SuggestionMonitoringCoordinator

新增 PoC 组合层 `M3SuggestionMonitoringCoordinator`。

它负责：

- 初始化现有 template/rule SQLite stores；
- 列出模板供 PoC 显式选择；
- 读取当前 display topology 供 `SelectedTarget` 选择；
- 手动 observation 后把 diagnostics 喂给 03C pipeline；
- 启动 / 停止既有 `M3ReadOnlyMonitoringSession`；
- 把 monitoring baseline prime 与 background runs 按正确顺序喂给 03C；
- observation run 真正 Failed 时把当前 Suggestion 标为 Suspended；
- baseline reset 时同时 reset observation baseline 与 suggestion evidence；
- 转发 raw observation diagnostics 和 Suggestion processing diagnostics 给 UI。

它仍然不依赖 Writer / ExecutionCoordinator。

## Startup prime ordering

### 风险

`M3ReadOnlyMonitoringSession.StartAsync()` 的顺序是：

```text
start FileSystem source
→ prime stable baseline
→ start pump + periodic
→ return StartResult
```

FileSystem source 可以在 `Start()` 后、甚至 prime 期间已经产生 hint。

pump 启动后可能在 `StartAsync()` 返回给 UI 之前就处理该 hint 并触发 background `RunCompleted`。

如果 UI 在 Start 返回后才处理 `BaselinePrime`，可能出现错误顺序：

```text
background ChangesDetected
→ publish new Suggestion
→ late BaselinePrime
→ BaselineReestablished retires that new Suggestion
```

### 解决

Coordinator 在调用 session Start 前进入 startup buffering：

```text
startupBuffering = true
→ session.StartAsync()
→ background RunCompleted 先进入 FIFO buffer
→ process BaselinePrime through Suggestion pipeline
→ startupBuffering = false
→ 按到达顺序放行 buffered runs
```

后续 background runs 通过单调 `_backgroundTail` 串行处理。

因此 Suggestion 解释顺序固定为：

```text
MonitoringPrime
→ Background #1
→ Background #2
→ ...
```

03C 自身仍保留 process single-flight，形成第二层防护。

## Observation run failure

02B `DesktopObservationTriggeredRunStatus.Failed` 表示连正常 diagnostics 都没有返回。

03D 扩展 Suggestion lifecycle suspension reason：

```text
ObservationRunFailed
```

当前 Suggestion 会进入 Suspended，而不是继续显示 Active。

## Baseline reset

用户点击“重置基线”时，03D 不再只 reset `DesktopStateBaselineTracker`。

同时立即：

```text
Suggestion → retired(BaselineReset)
```

原因是旧 Suggestion 的连续证据链在 Reset 瞬间已经失效，不应等下一次 BaselineEstablished 才从 UI 消失。

## Store initialization

Template/rule SQLite stores 都需要显式 `InitializeAsync()`。

Coordinator 使用 single-flight initialization gate，第一次 Suggestion 功能使用时同时初始化两者；后续 List/Build 复用同一实例。

后台能力因此不依赖“用户之前是否打开过模板编辑器”。

## Explicit PoC binding

Binding 仍然只在当前进程内存在，不持久化。

UI 提供：

- Template 下拉框；
- SelectedTarget 显示器下拉框；
- 应用绑定；
- 清除绑定；
- 忽略当前 Suggestion。

模板列表默认高亮第一项只是浏览便利，不会自动调用 `SetBinding()`。

### TargetDisplayBinding

- `PrimaryDisplay`：目标屏选择禁用；
- `ExactDeviceName`：目标屏选择禁用，继续使用模板固定设备名；
- `SelectedTarget`：必须明确选择当前 topology 中一块显示器后才能应用 binding。

当前仍不实现“自动选择模板”。未配置时 03C 返回 `NotConfigured`。

## Suggestion UI

显示：

- Binding revision；
- Active / Suspended / None；
- suspension / retirement reason；
- TemplateId；
- TargetDeviceName；
- MoveRequiredCount；
- ExecutionStepCount；
- UsesTemporaryStaging；
- SourceObservationId。

窗口明确说明：

> Suggestion 中 Candidate 只是当时的建议证据，不可直接执行。

本窗口不存在执行按钮。

## Manual / Monitoring 统一

Manual observation 现在也经 Coordinator：

```text
RunManualAsync
→ diagnostics runner
→ 03C pipeline
```

Monitoring background：

```text
M3ReadOnlyMonitoringSession.RunCompleted
→ Coordinator ordered tail
→ 03C pipeline
```

Monitoring 运行时 Manual / Reset 继续禁用，不绕过 02B single-flight。

## 自动测试

新增/更新：

- DiagnosticsWindow 构造只依赖 Coordinator，不暴露 Writer；
- Coordinator 构造不暴露 Writer；
- FileSystem source Start 当场发布 hint 时，Suggestion processing 顺序仍严格为 Prime → Background；
- Prime 建 baseline 后 background position change 可生成 Suggested；
- Manual first run → BaselineReestablished；
- Manual second changed run → Suggested；
- Reset baseline 立即退休 Suggestion 为 BaselineReset；
- template/rule stores 只初始化一次。

## 仍未实现

- binding SQLite persistence；
- automatic template selection policy；
- Windows system notification；
- tray Suggestion UX；
- user-confirmed fresh rebuild execution handoff；
- unattended Writer。

下一步建议进入 M3-04A：先定义 Notification policy / dedup / cooldown 与 notification sink port，再接 Windows 实际通知 surface。
