# M3-02G：Periodic Fallback Monitoring Integration

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI 与 Windows 实机验收
- 范围：M3-A，只读监控闭环

## 目的

把 M3-02F periodic fallback 接入 M3-02E 的显式只读监控会话，使当前 PoC 同时具备：

```text
FileSystemWatcher
→ 低延迟提示 Added / Removed / Rename 类变化

PeriodicFallback
→ 低频兜底没有可靠事件的变化
→ 包括纯桌面图标位置拖动
```

二者最终都只进入同一个：

```text
HintBatcher
→ BackgroundPump
→ single-flight BatchProcessor
→ Stable Observation
→ session baseline
→ State Diff
```

## Periodic source port

新增 `IPeriodicObservationHintSource`。

`M3ReadOnlyMonitoringSession` 只依赖该只读 port，不持有 clock，也不自己实现 tick 逻辑。

职责分离：

```text
PeriodicObservationHintSource
= 什么时候周期提醒一次

M3ReadOnlyMonitoringSession
= 这条长期 loop 什么时候开始/结束

BackgroundPump
= hint 什么时候形成 ready batch

DiagnosticsRunner
= 当前真实桌面是什么
```

## PoC interval

当前 Debug / PoC 配置：

```text
Periodic fallback interval = 15 s
```

选择 15 秒是为了先保守控制空闲 Shell COM 压力。

它不是正式产品默认值。

真实 Windows 验收后需要测量：

- full observation 耗时；
- CPU；
- 内存；
- Shell COM 抖动；
- 图标数量扩展后的成本；
- 15 秒位置变化发现延迟是否可接受。

再决定正式 interval。

## 启动顺序

监控启动保持 02E 的安全顺序：

```text
create batcher / processor / pump
→ start FileSystemWatcher source
→ pump 尚未运行
→ stable baseline prime
→ prime 成功
→ start pump + periodic loop
```

Periodic source 不在 baseline prime 前启动。

原因：

- periodic source 本来就是最终收敛兜底，不需要抢 baseline；
- source 自身也定义“先等待一个完整 interval，再发第一 tick”；
- baseline prime 已经负责建立当前启动事实。

FileSystemWatcher 则继续先于 baseline prime 启动，以缩小文件事件盲区。

## 同一 HintBatcher

Periodic 与 FileSystem hint 共享同一个 thread-safe batcher。

因此如果：

```text
FileSystem event
+ periodic tick
```

在同一个 quiet / max-delay 窗口内发生，它们可以自然合并为一个 ready batch，而不是重复执行两轮完整 Shell observation。

Batch diagnostics 会显示：

- hint 总数；
- FileSystem / PeriodicFallback 各自计数；
- provider source 集合。

## 生命周期绑定

Pump 与 periodic loop 被视为同一个长期 monitoring session 的两个必要组件。

```text
BackgroundPump
PeriodicFallback
```

任一组件在 session 仍 active 时非预期：

- 正常返回；
- 意外 cancellation；
- fault；

都视为监控能力降级。

处理：

1. 记录 component fault；
2. cancel shared session CTS；
3. Stop FileSystemWatcher source；
4. 等待 pump 与 periodic 两个 Task 都完整进入终态；
5. `IsRunning=false`；
6. 通知 diagnostics UI；
7. 用户可以重新 Start，创建新 batcher / pump / watcher，并重启 periodic loop。

不会出现“pump 死了但 periodic 还每 15 秒 tick”或“periodic 死了但 UI 还宣称拥有最终收敛能力”的半残状态。

## Stop race

手动 Stop 可能与一个 component completion/fault 几乎同时发生。

实现会再次检查 shared session cancellation state：

- 已进入正常 Stop 的 cancellation 不报告为监控 fault；
- 真正发生在 active session 中的非预期退出才报告 fault。

无论是否已有 fault，都会显式 await pump 与 periodic 两个 Task 到终态，再释放 CTS / watcher。

## Restart

同一个 `PeriodicObservationHintSource` 实例本身不保存 session 状态。

每次 monitoring Start 都会调用一个新的：

```text
RunAsync(..., fresh session CTS)
```

因此：

```text
Start
→ periodic loop #1
→ Stop / cancel
→ Start
→ periodic loop #2
```

不会复用已经取消的旧 Task。

## UI

Diagnostics Window 的说明更新为：

- FileSystemWatcher：新增 / 删除 / 重命名低延迟 hint；
- 15 秒 PeriodicFallback：最终重新观察，包括纯图标位置拖动等静默变化。

后台 trigger diagnostics 原本已经输出 hint kind/source，因此 periodic tick 无需增加第二套 UI 通路。

## 自动测试

在 02E session lifecycle 测试基础上新增：

- FileSystem source unavailable 时 periodic 不启动；
- baseline Unavailable / Unstable 时 periodic 不启动；
- Stable prime 后 periodic loop 启动；
- Stop 会 cancel periodic；
- periodic tick 通过共享 pump 触发 background observation；
- periodic batch kind/source 正确；
- position-only current observation 变化可由 periodic trigger 得到 `ChangesDetected`；
- Stop → Start 会启动新的 periodic loop；
- Start while running 不会重复启动 periodic；
- periodic 非预期 fault 会 fail-closed 整个 monitoring session，并停止 FileSystem source。

结构测试继续保证 `M3ReadOnlyMonitoringSession` 只新增 `IPeriodicObservationHintSource` 这个只读依赖，仍无 `IDesktopIconPositionWriter`。

## Windows 实机验收重点

### A. 文件事件路径

1. 开启后台监听；
2. 新建 / 重命名 / 删除桌面测试文件；
3. 预期在约 quiet window 后自动出现 FileSystem-triggered diagnostics。

### B. 纯位置拖动路径

1. baseline 已稳定；
2. 只拖动一个已有桌面图标，不改文件名；
3. FileSystemWatcher 不应立即产生假事件；
4. 最迟下一次 periodic fallback + quiet window 后自动 fresh-observe；
5. State Diff 预期出现 `Changed + Position`，跨屏时同时包含 `MonitorAssignment`。

### C. 空闲桌面

持续空闲至少 1 分钟：

- 约每 15 秒产生 periodic hint；
- fresh observation 应多数得到 `NoChanges`；
- 记录观察耗时、CPU 与内存，作为 interval 调优依据。

### D. 合批

在 periodic tick 前后制造一次文件变化：

- 若落在同一 batch window，预期只执行一轮 background observation；
- HintKinds 同时包含 FileSystem 与 PeriodicFallback。

## 当前结论边界

02G 后可以称为：

> 事件辅助 + 周期最终收敛的只读桌面监控 PoC

仍不能称为自动整理：

```text
Observation
→ Stability
→ Diff
→ Diagnostics
→ STOP
```

Suggestion policy、通知策略、用户授权、自动执行门和 unattended Writer 仍全部未接入。
