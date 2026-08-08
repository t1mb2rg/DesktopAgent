# M3-B03 Coordinator Execution Bridge

## 目的

B03 是 M3-B 第一条真正能够进入真实 Writer 链的路径，但 M3 不新增 Writer：

```text
B02 ReadyForCoordinatorPreWriteGate
→ B03 single-flight
→ recovery state recheck
→ existing TargetDisplayLayoutExecutionCoordinator
   → fresh capabilities
   → fresh topology
   → fresh desktop icon read
   → latest ExecutionPlan rebuild
   → M3 automatic commit-point scope gate
   → safety snapshot + readback
   → recovery checkpoint
   → existing Writer
   → verification / rollback / recovery
```

M3-B03 依赖 `TargetDisplayLayoutExecutionCoordinator`，不直接依赖 `IDesktopIconPositionWriter`。

## 为什么使用现有 ExecuteWithScopeGateAsync

M1/M2 已经留有：

`TargetDisplayLayoutExecutionCoordinator.ExecuteWithScopeGateAsync(restorePlan, scopeGate, ct)`

scope gate 在 Coordinator fresh reread + latest ExecutionPlan rebuild 后执行，因此自动化层可以在旧 B01 Candidate 之外，再审核真正即将进入 M2 事务的最新执行计划。

B03 不复制 topology / icon identity / Auto Arrange / stale current position 等 M2 检查；这些继续由现有 ExecutionPlanner / Coordinator fail closed。

## Commit point 语义

B03 把 Coordinator scope gate 通过视为一次自动执行的 **commit point**。

在 commit point 之前：

- lifecycle 变化；
- Runtime Revision 变化；
- Automatic 被 Pause / Cooldown / Fuse 降级；
- latest plan 超出 risk budget；

都会让 scope gate 拒绝，Writer=0。

commit point 通过之后，本次已经进入 M2 安全事务。之后即使 UI 切换 Pause，也不把当前事务半路硬切断；M2 会继续通过 safety snapshot / recovery checkpoint / verification / rollback 规则完整收口。Pause 影响后续新的 automatic attempt。

如未来需要真正的紧急停止，应设计独立 emergency-stop / cancellation 语义，而不是复用普通 Pause。

## Single-flight

B03 使用 `SemaphoreSlim` 非排队 single-flight：

- 当前已有 automatic execution 时；
- 新请求直接 `ExecutionAlreadyInProgress`；
- 不把旧 B02 decision 排队等待；
- 后续必须基于新的桌面事实重新走 05C/B01/B02。

## Coordinator 前 recovery recheck

B02 已经检查 unresolved recovery，但 B02→B03 之间仍有时间窗口。

B03 在调用 Coordinator 前再次 `ListUnresolvedAsync()`：

- state unavailable → fail closed；
- unresolved > 0 → Writer=0；
- 查询完成后再核 lifecycle/runtime revision。

## Commit-point gate

Coordinator latest ExecutionPlan 生成后，B03 同步重查：

- exact Active Suggestion + lifecycle Revision；
- exact Runtime Revision + `CanAttemptUnattendedExecution`；
- RestorePlan / ExecutionPlan target 和基本结构；
- latest move count；
- latest execution step count；
- latest temporary staging；
- fresh risk budget。

风险预算针对 **Coordinator 最新 ExecutionPlan** 重新计算，不复用 B02 assessment。

## Staging

B03 不硬编码“自动模式禁止 staging”。

如果最新 ExecutionPlan 使用 temporary staging：

- 用户 risk budget 禁止 → commit point 拒绝；
- 用户 risk budget 明确允许，同时 M2 Planner / Coordinator 判定 executable → 可以进入 M2 事务。

这样 M3-B 复用 M2 已验证的 cycle/staging 能力，而不是退化成只允许单 direct move。

## 原始 Coordinator 结果不被磨平

B03 result 保留完整 `TargetDisplayLayoutExecutionRunResult`：

- Completed
- CompletedNoOp
- FailedBeforeWrite
- FailedAndRolledBack
- FailedRollbackIncomplete
- PositionUncertain

以及 SafetySnapshotId、Forward/Rollback attempts、Verification、Recovery 信息。

B04 再基于这些真实结果更新 runtime Cooldown/Fuse；B03 不提前把复杂结果压成一个简单 bool。

## 自动测试

使用真实 `TargetDisplayLayoutExecutionCoordinator` + 现有测试 fakes：

- B02 not ready → Writer=0；
- unresolved recovery → Writer=0；
- runtime stale before Coordinator → Writer=0；
- Coordinator fresh reread 发现 stale RestorePlan → scope gate 未调用、Writer=0；
- runtime 在 replan→scope gate 窗口变化 → scope gate reject、Writer=0；
- latest execution risk reject → Writer=0；
- 全部通过 → 真实 existing Coordinator 调用 Scripted Writer，并返回 SafetySnapshot / Session；
- concurrent automatic attempt 不排队。

## 下一步

M3-B04：把 B03 原始 Coordinator result 映射回 Runtime Policy：

- Completed → RecordExecutionSuccess → cooldown；
- FailedBeforeWrite 与 Writer 后失败需要区分；
- Writer 后失败 / PositionUncertain 应计入 fuse；
- unresolved/manual recovery 状态必须阻止下一次自动尝试。

B04 仍不创建新 Writer，只负责执行结果反馈闭环。
