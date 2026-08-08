# M3-B02 Automatic Safety Gate

## 目的

B01 已经把旧 Suggestion Candidate 换成当前事实下的 fresh Candidate。B02 再做一次 Application 层的自动化安全判断：

```text
B01 ReadyForAutomaticSafetyGate
→ exact Suggestion lifecycle still current
→ exact Runtime Revision still current + Automatic
→ unresolved recovery == 0
→ fresh Candidate defensive invariants
→ fresh Candidate risk budget
→ final lifecycle/runtime recheck
→ ReadyForCoordinatorPreWriteGate
→ STOP
```

最后状态仍然不是 Writer 授权。

## 为什么再次检查 Runtime Revision

05C 的 RuntimeSnapshot 可能在 B01/B02 期间变 stale。

只比较 `EffectiveMode=Automatic` 不够。例如：

```text
Automatic Rev 8
→ Pause Rev 9
→ Resume Rev 10
→ 又回到 Automatic
```

旧 Rev 8 的 preflight 不能因为表面状态又相同而复活。因此 B02 要求当前 Runtime Revision 与 05C snapshot 完全一致。

## Recovery gate

B02 直接消费 Core `IRecoverySessionStore.ListUnresolvedAsync()`。

只要存在任何 unresolved recovery session：

```text
Executing / KnownIncomplete / PositionUncertain
→ automatic execution blocked
```

Recovery state 无法读取也 fail closed。

Recovery 查询跨 async 边界，因此查询完成后立即重新检查 lifecycle / runtime，再进入 Candidate risk 判断。

## Fresh Candidate risk

05B 原本可以从 Suggestion 计算风险。B02 增加 `IDesktopAutomationCandidateRiskEvidencePolicy`，直接从 B01 fresh Candidate 重新投影：

- MoveRequiredCount
- ExecutionStepCount
- UsesTemporaryStaging

SuggestionId 仍绑定当前 Active Suggestion，用于 evidence identity。

B02 不复用旧 05C RiskAssessment。

## Candidate defensive invariants

B02 额外确认 fresh Candidate：

- `IsExecutable=true`
- `IsNoOp=false`
- MoveRequiredCount > 0
- ExecutionStepCount > 0
- RestoreBlockedCount == 0
- RestoreMissingCount == 0
- Candidate / RestorePlan / ExecutionPlan target 一致
- target 与当前 Suggestion target 一致

这些检查不是重新实现 Planner，而是在跨自动化边界前验证 Planner 输出仍满足 B02 的输入契约。

## 为什么还需要 B03 pre-writer gate

B02 结束后到真正 Writer 之间仍然存在时间窗口。

现有 `TargetDisplayLayoutExecutionCoordinator.ExecuteWithScopeGateAsync()` 已经提供关键接缝：

1. fresh capabilities
2. fresh display topology
3. fresh desktop icon read
4. 重新生成最新 ExecutionPlan
5. **调用 scope gate**
6. safety snapshot + readback
7. recovery checkpoint
8. Writer

因此 B03 应在这个现成 scope gate 里做 Writer 前最后一厘米的同步检查，而不是绕过 Coordinator 或新建自动 Writer。

## 状态

- HandoffNotReady
- SuggestionNoLongerCurrent
- RuntimeChangedSincePreflight
- RuntimeBlocked
- RecoveryStateUnavailable
- UnresolvedRecoveryPresent
- CandidateInvariantRejected
- FreshRiskBudgetBlocked
- ReadyForCoordinatorPreWriteGate

## 自动测试

覆盖：

- 无 Writer / ExecutionCoordinator 直接依赖；
- non-ready handoff 不查询 recovery/risk；
- Suggestion stale；
- Runtime Revision stale；
- recovery query failure；
- unresolved recovery；
- recovery query 中 lifecycle/runtime race；
- fresh Candidate risk 重新投影；
- corrupt risk evidence fail closed；
- 全部通过时最多到 CoordinatorPreWriteGate。

## 下一步

M3-B03：复用 `TargetDisplayLayoutExecutionCoordinator.ExecuteWithScopeGateAsync()`，把 B02 result 接到现有 M2 执行链。

B03 必须继续 single-flight，并在 Coordinator 最新 ExecutionPlan 生成后、第一笔 Writer 前同步重查 runtime/lifecycle/plan scope。不得增加第二套 Writer 路径。
