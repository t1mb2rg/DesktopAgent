# M3-02A：Observation Hint Coalescing

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI
- 范围：M3-A，只读后台编排基础件

## 目的

为后续 Windows 事件源建立一个与桌面事实完全隔离的 hint 合并层。

事件只表达：

> 当前桌面可能发生了变化，值得稍后重新做 fresh observation。

事件本身不能回答：

- 当前有哪些桌面图标；
- 图标现在在哪里；
- 当前显示器拓扑是什么；
- Auto Arrange / IconSpacing 当前值是什么；
- 某个 Writer 是否真的成功。

这些都必须继续由 M3-01A → 01D 的 fresh observation pipeline 建立。

## 数据模型

新增 `DesktopObservationHint`：

- `Kind`
- `Source`
- `ReceivedAtUtc`

`Source` 只允许表示固定 provider 身份，例如：

- `DesktopFileSystemWatcher`
- `ShellChangeNotifier`
- `DisplaySettingsWatcher`

不得把文件路径、PIDL、图标坐标等事件 payload 当作 Source 塞入 hint。

Hint kind 当前定义：

- `Unknown`
- `FileSystem`
- `Shell`
- `DisplayTopology`
- `PeriodicFallback`
- `Manual`

## QuietPeriod + MaxBatchDelay

纯 debounce 有一个明显问题：

```text
hint → 重新计时
hint → 重新计时
hint → 重新计时
...
```

如果事件持续不断，重新观察可能永远被推迟。

因此 `DesktopObservationHintBatcher` 同时维护：

- `QuietPeriod`：最后一次 hint 后等待多久；
- `MaxBatchDelay`：从这一批第一条 hint 开始最多允许拖多久。

触发时间：

```text
DueAt = min(
    LastHintAt + QuietPeriod,
    FirstHintAt + MaxBatchDelay)
```

这样普通 burst 会等待安静，持续事件风暴也不会让 observation 永久饥饿。

## Batch 边界

如果一个新 hint 到达时旧 batch 已经到期：

1. 旧 batch 立即作为 `ReadyBatch` 返回；
2. 新 hint 成为下一 batch 的第一条；
3. 不允许跨过已经成立的触发边界继续吞并。

这避免 worker 因调度稍晚而把两个逻辑批次粘在一起。

## Batch 输出

`DesktopObservationHintBatch` 只保留用于调度/诊断的有界语义：

- FirstHintAtUtc
- LastHintAtUtc
- DueAtUtc
- TotalHintCount
- 每种 HintKind 的计数
- provider Source 去重集合

它不包含 DesktopObservation，不包含 Writer state，不包含历史恢复状态。

## 时间乱序

Hint timestamp 仅为调度诊断元数据。

若来源时间戳乱序或系统时钟发生小幅回拨，pending batch 的 `LastHintAtUtc` 不允许倒退，因此 DueAt 不会因为一条旧时间戳事件被错误提前。

## 生命周期

当前 batcher 是纯状态对象：

- 不启动线程；
- 不注册 Windows hook；
- 不执行 Task.Delay；
- 不自动调用 DiagnosticsRunner；
- 不调用 Writer。

它提供：

- `RegisterHint()`
- `TryTakeReady()`
- `TakePendingNow()`
- `Reset()`
- `NextDueAtUtc`

真正后台 worker 留给 M3-02B。

## 自动测试

覆盖：

- 非法 QuietPeriod / MaxBatchDelay；
- 单 hint quiet due；
- burst 延长 quiet due；
- event storm 被 max delay 截断；
- due 前不可取、due 时可取；
- late hint 不跨 batch 边界；
- kind count 与 distinct source；
- 乱序时间戳不让 batch 倒退；
- explicit drain；
- reset。

## 下一步 M3-02B

在这一层之上实现后台 driver：

```text
Windows hint source(s)
→ DesktopObservationHintBatcher
→ wait until DueAt
→ DesktopObservationDiagnosticsRunner.RunOnceAsync()
→ diagnostics/result sink
```

必须继续满足：

- single-flight，禁止并发跑多条完整 Shell observation；
- 运行期间来的新 hint 不得丢失；
- Unstable / Unavailable 不推进 baseline；
- event payload 不能直接进入 State Diff；
- unattended Writer gate 继续关闭。
