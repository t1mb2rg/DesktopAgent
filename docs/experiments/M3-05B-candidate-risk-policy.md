# M3-05B：Candidate Risk Budget Policy

## 目标

在 05A 已经能回答“当前运行状态是否允许 Automatic”的基础上，再单独回答另一件事：

```text
这份具体 Suggestion 的计划规模，是否落在用户允许的自动风险预算内？
```

05B 链路：

```text
DesktopLayoutSuggestion
→ project minimal risk evidence
→ user automation risk budget
→ WithinAutomationRiskBudget / block reasons
→ STOP
```

这仍然不是执行授权。

即使：

```text
05A runtime allows Automatic
AND
05B WithinAutomationRiskBudget = true
```

未来真实执行仍必须重新进入 fresh M2 safety chain。

## 为什么不重新判断 M2 可执行性

`DesktopLayoutSuggestion` 构造本身已经要求：

```text
Candidate.IsExecutable == true
Candidate.IsNoOp == false
```

M2 Candidate 又已经来自：

- rule-aware Preview；
- TargetSnapshot；
- RestorePlan；
- ExecutionPlan；
- M2 blocking / missing / plan legality checks。

因此 05B 不重新实现：

- RestoreBlockedCount 判定；
- RestoreMissingCount 判定；
- Auto Arrange 判定；
- topology / assignment / Planner legality；
- IsExecutable。

这些仍然属于 M2 Planner / safety chain。

05B 只做“用户愿意让自动模式承担多大计划”的风险预算。

## Risk Evidence

新增最小投影：

```text
DesktopAutomationCandidateRiskEvidence
```

只包含：

- SuggestionId；
- MoveRequiredCount；
- ExecutionStepCount；
- UsesTemporaryStaging。

生产路径：

```text
DesktopLayoutSuggestion
→ DesktopAutomationCandidateRiskEvidence.FromSuggestion()
```

Evidence 刻意不暴露：

- DesktopLayoutRuleAwareExecutionCandidate；
- RestorePlan；
- ExecutionPlan；
- Writer；
- ExecutionCoordinator。

这样风险策略不会逐渐长成第二套执行层。

## 用户风险预算

Settings：

```text
MaxMoveCount > 0
MaxExecutionStepCount > 0
AllowTemporaryStaging
```

### MaxMoveCount

```text
MoveRequiredCount <= MaxMoveCount
→ within this dimension

MoveRequiredCount > MaxMoveCount
→ MoveCountLimitExceeded
```

边界值等于上限时允许。

### MaxExecutionStepCount

Move 数和实际 execution step 数不是同一个量。

尤其 cycle / staging 计划可能：

```text
ExecutionStepCount > MoveRequiredCount
```

所以单独限制：

```text
ExecutionStepCount > MaxExecutionStepCount
→ ExecutionStepLimitExceeded
```

### AllowTemporaryStaging

默认产品值还没有在 05B 冻结。

策略语义只是：

```text
UsesTemporaryStaging == true
AND AllowTemporaryStaging == false
→ TemporaryStagingDisallowed
```

未来如果自动模式默认禁用 staging，只需要 composition/settings 选择 false，不需要改 Planner。

## Assessment

`DesktopAutomationCandidateRiskAssessment` 返回：

- Evidence；
- Settings；
- deterministic BlockReasons；
- `WithinAutomationRiskBudget`。

BlockReasons 固定顺序：

1. MoveCountLimitExceeded
2. ExecutionStepLimitExceeded
3. TemporaryStagingDisallowed

多个风险可以同时返回，不只报第一个。

例如：

```text
MaxMove = 2
MaxSteps = 3
AllowStaging = false

Candidate:
Moves = 4
Steps = 7
Staging = true
```

结果同时包含三项 blocker。

## 与 05A 的关系

05A 和 05B 回答不同问题：

```text
05A Runtime Policy
→ 当前运行状态允许 Automatic 吗？

05B Candidate Risk Policy
→ 这份具体 Suggestion 在用户风险预算内吗？
```

不能把两者压成一个 bool。

未来组合层至少需要：

```text
runtime.CanAttemptUnattendedExecution
AND risk.WithinAutomationRiskBudget
AND fresh M2 safety gate
```

才有资格继续向后讨论 execution。

当前 M3-A 下第一项始终会被 `M3AProvisionalClosed` 阻断。

## 自动测试

覆盖：

- count 恰好等于限制时通过；
- MoveCount 超限；
- ExecutionStepCount 独立超限；
- staging 禁止 / 显式允许；
- 三类风险同时超限时 deterministic 全量返回；
- 真实 DesktopLayoutSuggestion → Evidence 映射不改变 M2 Candidate 的统计值；
- RiskEvidence 只暴露 4 个原子属性；
- invalid settings / evidence fail closed。

## 不在范围

- 05A runtime mode；
- topology / Auto Arrange / stale Candidate 重验；
- recovery session gate；
- safety snapshot；
- execution rate limit；
- automation history；
- Writer。

## 下一步

M3-05C 建议做纯 `Automation Decision` 组合：

```text
05A RuntimeSnapshot
+ 05B CandidateRiskAssessment
→ AutomaticBlocked / CandidateWithinBudgetButRuntimeBlocked / ReadyForFutureFreshSafetyCheck
```

即使最后一项成立，也只表示“可以进入未来 fresh M2 safety preflight”，不执行 Writer。

在真实 M2 双屏验收完成前，05C 最终永远不能给出 unattended execution authorization。
