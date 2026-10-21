# M3-02E：PoC Read-only Monitoring Session

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI 与 Windows 实机验收
- 范围：M3-A，只读 PoC 组合与生命周期

## 目的

把已经分别验证的组件首次组合到 WPF PoC 中，但仍坚持显式人工启停：

```text
M3 Diagnostics Window
→ Start Background Monitoring
→ Desktop FileSystem hint source
→ HintBatcher
→ BackgroundPump
→ Single-flight BatchProcessor
→ Stable Observation
→ session baseline
→ State Diff
→ diagnostics UI
```

不会在普通 Debug 启动时自动开启 watcher。

用户必须在 M3 Diagnostics Window 中点击：

- `启动后台监听`
- `停止后台监听`

## 为什么先手动启停

这是第一次把真实 Windows event source 与完整 M3 observation pipeline 串起来。

在完成真实 Explorer 人工验收前，不应该把它悄悄变成常驻后台行为。

显式按钮可以清楚区分：

1. 手动 Observation 是否正常；
2. watcher 是否正常启动；
3. event hint 是否进入 batcher；
4. quiet/max-delay 是否正常；
5. background observation 是否被触发；
6. State Diff 是否正确。

## 启动顺序

监控会话采用一个刻意设计过的顺序：

```text
create fresh batcher / processor / pump
→ start FileSystemWatcher source
→ pump 暂不运行
→ run one stable baseline prime
→ if Stable: start pump
→ if Unstable / Unavailable: stop source and abort start
```

关键点是 source **先于 baseline prime 启动**。

Baseline prime 期间收到的文件系统事件会：

```text
watcher callback
→ pump.TryPublishHint(...)
→ hint 立即进入 thread-safe batcher
→ wake signal 被保留
→ pump 仍未消费
```

Baseline 成功后 pump 启动，再处理这些 pending hint。

这样可以缩小“先建立 baseline、再打开 watcher”之间的事件盲区。

## Baseline prime gate

后台监控只在 prime 得到当前 stable fact 后启动。

允许：

- BaselineEstablished
- NoChanges
- ChangesDetected

拒绝：

- Unstable
- Unavailable

拒绝时：

- watcher Stop；
- watcher Dispose；
- pending batch Reset；
- pump 不启动；
- baseline 不被错误推进。

## 手动操作与后台 single-flight

监控运行期间，Diagnostics Window 禁用：

- `观察一轮`
- `重置基线`

原因：这两个按钮会直接调用共享 `DesktopObservationDiagnosticsRunner`。

如果允许它们与 background pump 同时运行，会绕过 `DesktopObservationBatchProcessor` 的 single-flight gate，让两个 pipeline 并发更新同一个 session baseline。

因此 UI 明确禁止双线抢占 baseline。

## 可重启会话

`DesktopObservationBackgroundPump` 单实例本身只允许 Run 一次。

PoC 不强行给 pump 增加复杂的“重启自身”语义，而是每次 Start 创建新组件：

- new HintBatcher
- new BatchProcessor
- new BackgroundPump
- new DesktopFileSystemObservationHintSource

Stop 后这些对象全部释放。

下一次 Start 创建全新的监控组件，但共享 `DesktopObservationDiagnosticsRunner`，因此当前进程的 stable baseline 默认保留。

如果希望重新建立 baseline，先在监控停止状态下点击 `重置基线`。

## PoC 参数

当前 DI 配置：

```text
Stable observation sample interval = 500 ms
Hint quiet period = 700 ms
Hint max batch delay = 3 s
```

这些都是 PoC 配置，不是 Core 固定规则。

真实 Windows 验收后可按事件节奏调整。

## UI 输出

后台状态区显示：

- 未启动；
- 正在建立 baseline；
- running watcher count；
- baseline unavailable / unstable；
- stop；
- pump fault。

Diagnostics text 记录：

- source start result；
- 实际 watched directories；
- source issues；
- baseline prime result；
- trigger batch hint count / kinds / sources；
- triggered run duration；
- State Diff；
- escaped exception / pump fault。

## Pump fault

监控会话观察 pump lifetime。

如果 pump 因非 cancellation 异常退出：

- 保存 `LastPumpFault`；
- 停止 FileSystemWatcher source；
- `IsRunning=false`；
- 发布 `PumpFaulted` 给 UI；
- UI 可再次 Start，创建一套全新 session components。

单个 RunCompleted / PumpFaulted observer 自身异常不会反向杀死监控会话。

## Host disposal

`M3ReadOnlyMonitoringSession` 实现同步 `IDisposable`，与当前 PoC 的：

```text
_host.StopAsync().GetAwaiter().GetResult();
_host.Dispose();
```

兼容。

Dispose 会：

1. 停 watcher；
2. cancel pump；
3. 等待 pump lifetime 收口；
4. 退订事件；
5. dispose watcher / CTS / lifecycle gate。

Window 自身关闭时也会先尝试 `StopAsync()`，Host.Dispose 作为最终兜底。

## 自动测试

除原有 read-only dependency boundary 外，新增监控会话生命周期测试：

- hint source 全失败时不运行 baseline；
- baseline Unavailable 时 source 被 Stop + Dispose；
- baseline Unstable 时同样 fail-closed；
- Stable prime 后 session 正常运行；
- Stop 正确释放 source / pump；
- Stop → Start 创建新 hint source；
- restart 时 session baseline 默认保留；
- Start while running 幂等；
- baseline prime 期间到达的 hint 被 batcher 缓存，并在 pump 启动后触发下一轮 observation。

## Windows 实机验收计划

回到 Windows 11 主力机后：

### A. 启动

1. Debug 启动 PoC；
2. M3 Diagnostics Window 自动出现；
3. 点击 `启动后台监听`；
4. 预期 watcher count >= 1；
5. 预期 baseline prime 为 Stable 状态；
6. 后台状态显示运行中。

### B. 新建文件自动触发

1. 后台监听运行中；
2. 在桌面新建 `M3-monitor-test.txt`；
3. 不点击任何 DesktopAgent 按钮；
4. 等待 quiet period；
5. 预期 diagnostics 自动出现 background trigger；
6. State Diff 预期包含 Added。

### C. 重命名

1. 重命名测试文件；
2. watcher 发 hint；
3. fresh observation 后应由 StableId 真实语义决定结果，不使用 FileSystemWatcher rename payload 直接构造 Diff。

### D. 删除

1. 删除测试文件；
2. 自动 background trigger；
3. State Diff 预期包含 Removed。

### E. Stop / restart

1. 点击 `停止后台监听`；
2. 新建/删除文件不应触发 background diagnostics；
3. 再次点击 `启动后台监听`；
4. 新 watcher 正常启动；
5. baseline 默认延续当前 session 最近一次 stable fact。

### F. 手动按钮互斥

后台运行时：

- `观察一轮` disabled；
- `重置基线` disabled；
- `停止后台监听` enabled。

停止后手动按钮恢复。

## 已知覆盖缺口

当前自动监控仍听不到“纯图标位置拖动”。

因此本阶段只能称为：

> Desktop file-system event assisted monitoring

不能称为完整 desktop change monitoring。

下一步应增加 periodic fallback 或 Shell-specific hint，使位置变化最终也能触发 fresh observation。

## 安全边界

本 PR 仍然：

```text
Event Hint
→ Stable Observation
→ Baseline
→ State Diff
→ Diagnostics
→ STOP
```

没有 Suggestion 自动执行，没有 unattended Writer，也没有绕过 M2 gate。
