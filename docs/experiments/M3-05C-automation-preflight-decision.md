# M3-05C：Automation Preflight Decision

## 目标

把 05A Runtime Policy 与 05B Candidate Risk Budget 组合成一个明确、只读、不会被误解成执行授权的 preflight decision：

```text
05A RuntimeSnapshot
+ current Suggestion lifecycle
+ 05B RiskPolicy
→ Automation Preflight Decision
→ at most ReadyForFreshSafetyCheck
→ STOP
```

05C 不读取 Shell、不 fresh rebuild Candidate、不调用 Writer。

## 为什么输入是当前 Suggestion lifecycle

不能只把：

```text
runtime bool
+ 一份旧 RiskAssessment
```

做 AND。

Suggestion 本身有生命周期：

- None；
- Active；
- Suspended；
- replaced / retired。

05C 直接读取当前 `DesktopLayoutSuggestionLifecycleSnapshot`。

只有：

```text
State == Active
AND CurrentSuggestion != null
```

才调用 05B RiskPolicy。

### None

```text
→ NoCurrentSuggestion
→ 不调用 RiskPolicy
```

### Suspended

```text
→ SuggestionNotActionable
→ 不调用 RiskPolicy
```

这样不会出现：

```text
Suggestion 已 Suspended
但 diagnostics 仍显示 risk passed
```

这种容易误读的组合。

## RiskPolicy seam

05C 新增只读接口：

```text
IDesktopAutomationCandidateRiskPolicy
    Evaluate(DesktopLayoutSuggestion)
```

05B 的 concrete `DesktopAutomationCandidateRiskPolicy` 实现该接口。

接口只为了组合层依赖和可测试性，不增加任何 Writer 能力。

## Risk evidence 一致性复核

即使 RiskPolicy 是另一种实现，05C 也不会盲目信任返回结果。

返回的 RiskAssessment Evidence 必须与当前 Active Suggestion 完全匹配：

- SuggestionId；
- MoveRequiredCount；
- ExecutionStepCount；
- UsesTemporaryStaging。

任意不一致：

```text
throw InvalidOperationException
→ preflight fail closed
```

因此不能拿旧 S1 的 risk evidence 搭配当前 S2。

## Decision 状态

定义：

### NoCurrentSuggestion

当前 lifecycle 为 None。

### SuggestionNotActionable

当前有 Suggestion，但 lifecycle 为 Suspended。

### RuntimeBlocked

```text
05A CanAttemptUnattendedExecution == false
05B WithinAutomationRiskBudget == true
```

例如当前真实 M3-A：

```text
Requested Automatic
+ M3AProvisionalClosed
→ RuntimeBlocked
```

### RiskBudgetBlocked

```text
05A runtime ready
05B risk blocked
```

### RuntimeAndRiskBlocked

两层都 blocked。

结果保留完整 RuntimeSnapshot + RiskAssessment，方便 diagnostics 展示两类原因。

### ReadyForFreshSafetyCheck

只在：

```text
current Suggestion = Active
AND 05A runtime ready
AND 05B risk within budget
```

时出现。

这个名字故意不是：

- ReadyToExecute；
- Authorized；
- ExecuteNow。

它只表示：

```text
可以继续进入未来 fresh M2 safety preflight
```

仍然没有 Writer 授权。

## 当前 M3-A 的实际行为

composition root 未来仍必须使用：

```text
DesktopAutomationUnattendedGate.M3AProvisionalClosed
```

所以真实当前环境即使：

```text
Active Suggestion
risk within budget
Requested Automatic
```

05C 也只能：

```text
RuntimeBlocked
```

测试里的 `ValidatedOpen` 仅用于证明未来 gate 真打开后的组合语义。

## Result

`DesktopAutomationPreflightDecision` 包含：

- Status；
- RuntimeSnapshot；
- SuggestionLifecycleSnapshot；
- optional RiskAssessment；
- SuggestionId；
- `MayProceedToFreshSafetyCheck`。

`MayProceedToFreshSafetyCheck` 只有 `ReadyForFreshSafetyCheck` 为 true。

## 自动测试

覆盖：

- None 不调用 RiskPolicy；
- Suspended 不调用 RiskPolicy；
- 当前 M3-A closed gate + risk pass → RuntimeBlocked；
- validated runtime + risk block → RiskBudgetBlocked；
- runtime + risk 同时 block；
- 两层都 pass → 只到 ReadyForFreshSafetyCheck；
- Requested Suggestion 在 validated gate 下仍 RuntimeBlocked；
- RiskPolicy 返回 mismatched Suggestion evidence → fail closed。

05B 的数值预算正确性继续由 05B 自己测试；05C fake risk policy 只测试组合状态。

## 不在范围

- fresh Shell reread；
- Candidate rebuild；
- recovery session check；
- safety snapshot；
- rate limit；
- automation history；
- Writer / ExecutionCoordinator；
- unattended execution authorization。

## 下一步

M3-05D 可以开始做 `Fresh Safety Preflight Contract`，但在真实 M2 双屏验收未完成前，只应定义：

```text
ReadyForFreshSafetyCheck
→ required fresh facts / future handoff contract
→ provisional gate still stops before execution
```

另一条安全的方向是先做 automation history / policy diagnostics 持久化，让 05A/05B/05C 的决策可解释、可审计。

考虑当前 M2 双屏实机门仍未完成，后者更适合作为近期继续推进的 M3-A 工作。
