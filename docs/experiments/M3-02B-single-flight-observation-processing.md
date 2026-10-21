# M3-02B：Single-flight Observation Batch Processing

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI
- 范围：M3-A，只读后台编排基础件

## 目的

在 M3-02A 的 ready hint batch 与 M3-01D diagnostics pipeline 之间建立单航班执行边界：

```text
Ready Hint Batch
→ DesktopObservationBatchProcessor
→ DesktopObservationDiagnosticsRunner
→ one complete observation pipeline at a time
```

即使上层同时提交多个 ready batches，也禁止并发执行完整 Shell observation。

## 为什么必须 single-flight

DesktopAgent 的 fresh observation 会读取：

- display topology；
- desktop positioning capabilities；
- 完整 Shell desktop items；
- 第二次 topology；
- 第二轮完整 observation 用于 stability。

如果多个事件 batch 并发跑这条链：

- Shell COM 压力会被无意义放大；
- 两条 pipeline 可能看到彼此交错的桌面时刻；
- baseline 更新顺序会变得不确定；
- 后续 notification/suggestion 可能出现重复或倒序。

因此 processor 使用单实例 `SemaphoreSlim(1,1)`，把所有 batch 串行化。

## Triggered run result

新增 `DesktopObservationTriggeredRun`：

- RunId
- TriggerBatch
- StartedAtUtc
- CompletedAtUtc
- Duration
- Status
- DiagnosticsResult 或 EscapedException

状态只区分：

- `Completed`
- `Failed`

### Completed 的含义

Completed 表示 diagnostics pipeline 正常返回。

它的 `DiagnosticsResult.Status` 仍可能是：

- BaselineEstablished
- NoChanges
- ChangesDetected
- Unstable
- Unavailable

其中 `Unstable` / `Unavailable` 是正常的显式业务结果，不是后台线程异常。

### Failed 的含义

只有 diagnostics pipeline 之外的逃逸异常才成为 Failed。

异常会转成诊断字符串保留，processor 自身继续可用于下一 batch，不因一次 Shell / infrastructure 异常永久失效。

## Cancellation

如果调用方 cancellation token 已取消：

- 等待 single-flight gate 时正常抛 `OperationCanceledException`；
- diagnostics pipeline 内因同一 token 取消时也继续向外传播；
- 不把服务停止误记成 Failed。

## 与 HintBatcher 的职责边界

`DesktopObservationBatchProcessor` 不负责：

- QuietPeriod；
- MaxBatchDelay；
- timer；
- Windows event registration；
- hint retention；
- Writer。

它只消费已经 ready 的 `DesktopObservationHintBatch`。

因此：

```text
02A HintBatcher = 什么时候该触发
02B BatchProcessor = 一次只能跑一个
01D DiagnosticsRunner = 这一轮实际看到了什么
```

## 自动测试

覆盖：

- 两个并发 batch 的 diagnostics 最大并发数始终为 1；
- 第一轮结束后第二轮继续执行；
- 逃逸异常返回 Failed；
- Failed 后下一轮仍可 Completed；
- 显式 Unavailable 属于 Completed；
- 等待 single-flight gate 时 cancellation 正常传播；
- diagnostics 内 cancellation 正常传播。

## 下一步 M3-02C

下一层再建立真正的后台 pump：

```text
hint input
→ 02A batcher
→ wait/wakeup
→ ready batch
→ 02B processor
→ result sink
```

必须继续保证：

- processor 运行期间到达的新 hint 不丢失；
- due timer 与新 hint 能互相唤醒；
- stop/cancellation 可终止等待；
- 后台异常不杀死整个 PoC；
- event payload 仍不进入 DesktopObservation / State Diff；
- unattended Writer gate 继续关闭。
