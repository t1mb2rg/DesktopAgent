# M3-03C：Suggestion Pipeline

## 目标

把 M3-01/02 的 observation diagnostics、M3-03A Candidate-backed Suggestion Builder 与 M3-03B Suggestion Lifecycle 串成一条只读建议解释管线：

```text
DesktopObservationDiagnosticsResult
→ lifecycle interpretation
→ explicit in-memory binding
→ M3-03A Builder
→ stale-binding revision gate
→ M3-03B Publish / Suspend / Retire
→ STOP
```

本阶段不接系统通知、不接用户执行入口、不调用 Writer / ExecutionCoordinator。

## Builder port

新增 `IDesktopLayoutSuggestionBuilder`，由既有 `DesktopLayoutSuggestionBuilder` 实现。

目的不是复制 Builder，而是让 pipeline：

- 只依赖只读建议构建能力；
- 能用 delayed/fake builder 精确测试 async binding race；
- 不知道 template/rule store 的基础设施细节。

## Binding

Pipeline 当前维护进程内：

```text
BindingRevision
DesktopLayoutSuggestionBinding?
```

`SetBinding`：

- 值实际变化才 revision +1；
- 同值重复设置为 no-op；
- binding 一旦变化立即 `MarkBindingChanged()`，退休旧 Suggestion；
- `null` 表示 NotConfigured。

本阶段不持久化 binding，也不自动挑模板。

## Diagnostics → lifecycle 映射

### BaselineEstablished

```text
MarkBaselineReestablished(currentObservationId)
```

如果之前存在 Suggestion，会退休。首次启动没有 Suggestion 时是 no-op。

### NoChanges

```text
ConfirmNoDesktopChanges()
```

Active 继续 Active；因 Unstable/Unavailable 暂停的 Suggestion 恢复 Active。

新的 ObservationId 本身不造成 stale。

### Unstable

```text
Suspend(ObservationUnstable)
```

### Unavailable

```text
Suspend(ObservationUnavailable)
```

### ChangesDetected

先做：

```text
MarkDesktopChanged(currentObservationId)
```

因此旧 Suggestion 在异步 template/rule 读取开始前就已经退休。

随后：

- 没有 binding → `NotConfigured`；
- 有 binding → 调 03A Builder；
- Suggested → publish 新 Suggestion；
- NoActionNeeded → 保持 None；
- 其他 builder reject → `BuildRejected`，保持 None；
- builder escaped exception → `BuilderFailed`，保持 None。

旧 Suggestion 不会因为新 build 失败而复活。

## Binding revision stale gate

异步 Build 可能经历：

```text
capture binding rev=7
→ await template/rule store
→ user changes binding to rev=8
→ old rev=7 build completes
```

Pipeline 在最终 publish 前重新进入同一个 binding lock，并验证：

```text
current revision == captured revision
AND
current binding == captured binding
```

只有仍一致才允许 `lifecycle.Publish()`。

若不一致：

```text
BindingChangedDuringBuild
```

旧结果仅作为 diagnostics result 返回，不发布成当前 Suggestion。

最终 revision check 与 `Publish()` 在同一个 binding lock 中完成，避免“检查通过后、publish 前 binding 又变化”的窄竞态窗口。

`SetBinding` 与最终 publish 都遵循固定锁顺序：

```text
binding gate → lifecycle gate
```

## Process single-flight

Pipeline 自身使用 `SemaphoreSlim` 串行解释 diagnostics。

即使未来出现 monitoring 之外的第二个调用者，也不会让两份 State Diff 并发修改 lifecycle。

这与 M3-02B 的 Shell observation single-flight 属于不同层次：

- 02B 保证完整桌面观察一次只跑一个；
- 03C 保证建议状态解释一次只跑一个。

## Result 状态

- BaselineReestablished；
- NoChanges；
- ObservationUnstable；
- ObservationUnavailable；
- NotConfigured；
- Suggested；
- NoActionNeeded；
- BuildRejected；
- BindingChangedDuringBuild；
- BuilderFailed。

Result 同时携带：

- 原 Diagnostics；
- binding snapshot + revision；
- optional 03A BuildResult；
- 03B Lifecycle snapshot；
- builder escaped exception（仅 BuilderFailed）。

## Read-only boundary

`DesktopLayoutSuggestionPipeline` 构造依赖只有：

- `IDesktopLayoutSuggestionBuilder`；
- `DesktopLayoutSuggestionLifecycle`。

不得依赖：

- `IDesktopIconPositionWriter`；
- `TargetDisplayLayoutExecutionCoordinator`；
- Recovery store；
- WPF / WindowsInterop。

## 自动测试

覆盖：

- pipeline 构造不暴露 Writer；
- equal binding 不增加 revision；
- BaselineEstablished 退休旧建议；
- NoChanges 恢复 suspended suggestion；
- Unstable / Unavailable suspend；
- ChangesDetected + no binding → retire old + NotConfigured；
- ChangesDetected + real 03A Builder → Active Suggestion，SourceObservationId 与当前 diagnostics 一致；
- NoActionNeeded → None；
- rejected build → None；
- binding 在 async build 中改变 → old Suggested result 丢弃；
- escaped builder exception 被收敛为 BuilderFailed。

## 下一步

M3-03D 建议解决“显式 binding 从哪里来”：

- 先提供 PoC / settings 中明确的 TemplateId + optional SelectedTargetDeviceName；
- 再决定是否持久化为 SQLite automation settings；
- monitoring run result 接入 03C pipeline；
- UI 展示 Active/Suspended Suggestion。

之后 M3-04 再做真正 Notification。

无论 UI 如何发展，用户点击建议后的执行仍必须 fresh reread + fresh rebuild 进入既有 M2 安全执行链；当前 unattended Writer gate 不变。
