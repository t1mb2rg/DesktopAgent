# M3-B04 Execution Outcome Feedback

## 目的

B03 已经能通过现有 M2 Coordinator 完成一次受控 automatic execution。B04 把 Coordinator 的真实结果反馈回 05A Runtime Policy：

```text
B02 SafetyGate
→ B03 Execute exactly once
→ Coordinator returned?
   ├─ no  → runtime 不记 execution outcome
   └─ yes → IsSuccess ? RecordExecutionSuccess : RecordExecutionFailure
→ cooldown / consecutive failures / fuse
```

B04 不创建 Writer，也不改变 M2 execution/recovery 语义。

## 为什么不提供 Apply(oldResult)

如果暴露：

```text
Apply(B03Result)
```

调用方可能误把同一份旧结果应用两次，导致：

- failure counter 多加一次；
- fuse 被提前触发；
- cooldown / revision 被重复推进。

因此 B04 使用 `DesktopAutomationExecutionOutcomeCoordinator.ExecuteAndRecordAsync()`，把“执行一次”和“反馈一次”绑定在同一调用链里。

没有 public `Apply(result)` 入口。

## 哪些情况记 execution outcome

只有 B03 真正调用 Coordinator 并得到 `TargetDisplayLayoutExecutionRunResult` 才记录。

B03 在 Coordinator 前被挡住，例如：

- B02 not ready；
- runtime stale；
- unresolved recovery；
- concurrent automatic attempt；

都不记 execution success/failure，因为没有发生 Coordinator execution attempt。

## Success

`CoordinatorRun.IsSuccess == true`：

- Completed
- CompletedNoOp

→ `RecordExecutionSuccess()`

结果：

- LastExecutionOutcome = Success
- 进入 execution cooldown（若配置 > 0）
- fuse 尚未 trip 时清连续失败数
- 已经 trip 的 fuse 不会被成功静默复位

NoOp 也算一次成功的安全尝试，因为 Coordinator 已经 fresh reread 并确定当前无需移动。

## Failure

当前 B04 采用保守规则：

任何 `CoordinatorRun.IsSuccess == false` 都：

→ `RecordExecutionFailure()`

包括：

- FailedBeforeWrite
- FailedAndRolledBack
- FailedRollbackIncomplete
- PositionUncertain

原因：即使 FailedBeforeWrite 没有真实 Writer，重复自动尝试也不应该高速撞同一个 stale/scope/snapshot 条件。记录 failure 可以让 cooldown 降速，并在连续失败后触发 fuse。

后续如果有足够实机数据，可以再细分“政策拒绝”和“执行失败”的不同 fuse 权重；B04 先保守 fail-safe。

## 连续失败与旧票

B04 单元测试可以直接注入三次 failure 来验证 Runtime Policy 的反馈规则。

生产路径不同：每次 `RecordExecutionFailure()` 都会推进 Runtime Revision，并可能进入 cooldown。因此下一次真实 automatic attempt 必须重新：

```text
05C
→ B01 fresh observation/candidate
→ B02 fresh safety gate
→ B03 Coordinator
→ B04 feedback
```

旧 B02 safety result 不会在生产里被重复用来连续撞 fuse。

## Bridge exception

如果 B03/bridge 自身发生逃逸异常并没有返回结构化 Coordinator result：

- B04 不猜 Writer 是否发生；
- 异常继续向上抛；
- Runtime outcome 不在 B04 中推进。

这类异常需要在 B05 orchestration 中 fail closed，并结合 recovery state 决定后续动作。不能靠 B04 猜测执行历史。

## 自动测试

覆盖：

- B04 不直接依赖 Writer；
- bridge 在 Coordinator 前停止 → Runtime 不变；
- Completed → Success + cooldown；
- CompletedNoOp → Success + cooldown；
- FailedBeforeWrite → 保守 Failure + cooldown；
- 连续 failure 达阈值 → fuse trip；
- fuse trip 后的 success 不自动 reset fuse；
- bridge escaped exception → 不猜 outcome、不推进 Runtime。

## 下一步

M3-B05 Automatic Execution Cycle：把现有 05C → B01 → B02 → B04 串成一次完整 automatic attempt，并接到 Suggestion lifecycle 事件。

当前真实 composition root 仍使用 `M3AProvisionalClosed`，所以 B05 可以完成代码与自动测试，但在真实 M2 双屏 + M3-B acceptance 之前不会默认触发 unattended Writer。
