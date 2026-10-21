# M3-02C：Background Observation Pump

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI
- 范围：M3-A，只读后台编排

## 目的

把 M3-02A hint batcher 与 M3-02B single-flight processor 串成真正可常驻运行的后台 pump，但暂时仍不接具体 Windows 事件源。

```text
hint publish
→ thread-safe HintBatcher
→ lightweight wake signal
→ due / ready batch
→ single-flight BatchProcessor
→ M3-01D DiagnosticsRunner
→ RunCompleted
```

## 关键设计：不排 raw event 队列

最初可选方案是：

```text
Windows events
→ Channel<raw hint>
→ worker 再 batch
```

这个方案在事件风暴下会先把大量 raw hint 堆入内存。

当前实现改为：

```text
PublishHint()
→ 调用线程直接进入 thread-safe DesktopObservationHintBatcher
→ wake channel 只放一个“重新检查状态”的信号
```

因此重复 wake signal 可以折叠，真正的事件数量由 batcher 立即压缩。

## 两类内部信号

Pump 内部只保留：

### Wake signal

- bounded capacity = 1；
- `DropWrite`；
- 语义只是“batcher 状态可能变化”；
- 重复 wake 丢弃是安全的，因为真实状态已经在 batcher 中。

### Ready batch queue

如果 `RegisterHint()` 发现旧 batch 在新 hint 到达前已经 due：

- 旧 batch 已经跨过逻辑边界；
- 它进入 ready batch queue；
- 新 hint 已经开始新的 pending batch。

Ready batch 不再重新参与 debounce。

## Due timer 与 wake race

当存在 pending DueAt 时，pump 同时等待：

```text
DelayUntil(DueAt)
vs
WakeSignal
```

两者必须共享同一个临时 cancellation scope。

谁先完成，立即取消另一个。

原因：如果 timer 先完成，但旧的 `ReadAsync()` 留在后台，它可能在下一轮偷偷消费新的 wake signal，造成极难复现的“偶尔不触发”。

当前实现显式取消 losing wait，并等待其 cancellation 被观察。

## 运行期间的新 hint

BatchProcessor 正在执行完整 observation 时，`TryPublishHint()` 仍可以从其他线程继续调用。

新 hint：

- 不进入当前已经 ready 的 batch；
- 直接进入 batcher 的下一 pending batch；
- 更新新的 DueAt；
- pump 完成当前 single-flight run 后再处理下一批。

因此 observation 运行期间的变化不会丢失，也不会与当前 batch 并发执行。

## RunCompleted

Pump 保存 `LastRun`，并提供 `RunCompleted` 事件。

事件发生在 pump 工作线程，不承诺 WPF Dispatcher。

每个 subscriber 单独 try/catch：

- 一个 observer 抛异常不会阻止其他 observer；
- observer 异常不会杀死 pump；
- 后续 telemetry 可以增加 observer-failure 计数。

## 生命周期

`RunAsync(ct)`：

- 单实例只能启动一次；
- cancellation 会结束等待/处理并向外传播；
- finally 后停止接收新 hint；
- stop 后 `TryPublishHint()` 返回 false。

当前没有重启同一个 pump 实例的语义；host 若需要重启应创建新实例。

## 自动测试

使用可控 `ManualPumpClock`，避免真实 sleep 造成 flaky test。

覆盖：

- 第一条 hint 在 QuietPeriod 到期前不运行；
- 到期后准确触发 ready batch；
- 新 hint 更新 DueAt，旧 timer/wake 不丢信号；
- processor 运行期间的 hint 被保留到下一 batch；
- 下一 batch 的 hint count / kind count 正确；
- observer 抛异常不杀 pump、不影响其他 observer；
- pump 只能启动一次；
- stop 后拒绝新 hint。

## 当前仍未做

本 PR 不接：

- FileSystemWatcher；
- Shell change notification；
- display settings notification；
- periodic fallback timer；
- tray；
- notification / suggestion；
- unattended Writer。

## 下一步 M3-02D

下一步开始接第一个真实 Windows hint source。

优先建议先接风险最低、语义最明确的来源，再逐步扩展：

1. Desktop file-system change hint；
2. display topology/settings hint；
3. Shell-specific hint；
4. periodic fallback 兜底。

任何事件源都只能调用：

```text
pump.TryPublishHint(kind, providerName)
```

不得把事件 payload 直接写入 baseline / diff。
