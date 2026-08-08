# M3-B05 Automatic Execution Cycle

## 目的

B01-B04 已经分别完成 fresh boundary、automatic safety、M2 Coordinator bridge 和 Runtime outcome feedback。B05 把它们串成一次完整 automatic attempt：

```text
current Runtime + current Suggestion lifecycle
→ 05C Preflight
→ B01 Fresh Execution Handoff
→ B02 Automatic Safety Gate
→ B04 Execute-and-Record
   → B03 existing M2 Coordinator
→ Runtime cooldown / fuse
```

B05 不增加新安全规则，也不直接依赖 Writer。

## 固定顺序，不跨级补票

`DesktopAutomationExecutionCycleCoordinator.RunCurrentAsync()` 固定：

1. 读取当前 RuntimeSnapshot + SuggestionLifecycleSnapshot；
2. 05C；
3. 只有 `ReadyForFreshSafetyCheck` 才进 B01；
4. 只有 `ReadyForAutomaticSafetyGate` 才进 B02；
5. 只有 `ReadyForCoordinatorPreWriteGate` 才进 B04/B03；
6. 原样返回每一层结果。

任一阶段停住时，后续阶段不调用。

## Cycle single-flight

B03 自己已有 execution single-flight，但 B05 再提供 cycle-level single-flight：

- 一次 cycle 可能包含 fresh stable Observation / DB recovery query 等耗时步骤；
- 第二个 cycle 不排队等待旧证据变 stale；
- 直接返回 `CycleAlreadyRunning`；
- 后续由新的 Suggestion/monitoring 事实重新触发。

## Escaped fault fail-close

如果 05C/B01/B02/B04 任意阶段抛出非 cancellation 逃逸异常：

```text
→ 不猜 Writer 历史
→ 不伪造 Success/Failure
→ Runtime.Pause(indefinite)
→ FaultedAndPaused
```

Pause reason 明确要求人工检查后再恢复 Automatic。

`OperationCanceledException` 在调用方 token 取消时继续传播，不被转换成 generic fault；已进入 M2 transaction 的复杂取消/恢复仍由 Coordinator 自己结构化收口。

## Success 后不提前退休 Suggestion

B05 不在执行成功后直接调用 lifecycle retire/dismiss。

原因：

```text
Coordinator success
≠ 可以跳过之后的真实 Observation
```

正确顺序是：

1. B04 立刻记录 success + cooldown；
2. monitoring 随后观察真实桌面；
3. Suggestion pipeline 根据最新事实推进 lifecycle；
4. 模板已满足时自然 NoActionNeeded / retire。

这样仍然遵守“Shell/Observation 是现实，状态只是意图和证据”。

## 当前 real gate 仍保持 closed

即使 B05 代码完整，只要 composition root 仍使用：

`DesktopAutomationUnattendedGate.M3AProvisionalClosed`

05C 就会在 RuntimeBlocked 停止，B01/B02/B03 都不会被调用。

因此 B05 可以进入代码库和自动测试，而不会因为“自动 cycle 存在”就默认开启真实 unattended Writer。

## 自动测试

### 顺序/停止测试

覆盖：

- 当前 provisional gate → 05C 停止，后续调用数 0；
- B01 stop → B02/B04 不调用；
- B02 stop → B04 不调用；
- full ready path 固定 B01→B02→B04 顺序；
- escaped fault → indefinite Pause，不猜 outcome；
- caller cancellation 传播且不自动 Pause；
- concurrent cycle 不排队。

### 整链台架测试

额外使用真正的：

- `DesktopAutomationPreflightDecisionPolicy`
- `DesktopAutomationFreshExecutionHandoff`
- `DesktopAutomationAutomaticSafetyGate`
- `DesktopAutomationExecutionOutcomeCoordinator`
- `DesktopAutomationCoordinatorExecutionBridge`
- `TargetDisplayLayoutExecutionCoordinator`
- `ScriptedDesktopIconPositionWriter`

完整跑：

```text
Automatic
→ ReadyForFreshSafetyCheck
→ fresh Observation / fresh Candidate
→ ReadyForAutomaticSafetyGate
→ ReadyForCoordinatorPreWriteGate
→ existing M2 Coordinator
→ Scripted Writer
→ Completed
→ RecordExecutionSuccess
→ Cooldown
```

并验证：

- SafetySnapshotId 存在；
- Session 存在；
- Writer attempt count 与 fresh ExecutionPlan 一致；
- Runtime 进入 cooldown / Effective Suggestion；
- Suggestion lifecycle 未被 B05 乐观篡改。

## 下一步

M3-B06 PoC Automatic Trigger Integration：把 B05 cycle 接到 `M3SuggestionMonitoringCoordinator.SuggestionProcessed` / tray mode UI。

但真实 composition root 仍默认使用 provisional closed gate。B06 首先实现：

- SuggestionProcessed 只作为“可以尝试一次 cycle”的触发；
- cycle 自己仍重新读取 current runtime/lifecycle；
- Automatic 关闭/Paused/Cooldown/Fuse 时 05C 自然停止；
- 不根据 FileSystem hint 或 periodic tick 直接写；
- 受控 Debug/acceptance 开关才能使用 ValidatedOpen。
