# M3-05A：Automation Runtime Policy Foundation

## 目标

在 M3-A 仍然禁止无人值守真实写入的前提下，先把自动化运行状态和安全降级语义冻结：

```text
Requested mode
+ environment unattended-write gate
+ Pause
+ execution Cooldown
+ consecutive-failure Fuse
→ Effective mode
→ deterministic block reasons
→ STOP
```

05A 只做 Application 层纯状态机：

- 不读取 Shell；
- 不接收 M2 Candidate；
- 不调用 Writer；
- 不启动 / 停止 monitoring；
- 不执行任何桌面副作用。

## Requested Mode 与 Effective Mode

自动模式必须区分：

```text
用户希望程序怎么工作
!=
程序当前实际被允许做到哪一层
```

定义：

- Manual
- Suggestion
- Automatic

`RequestedMode` 是用户/配置意图。

`EffectiveMode` 是结合当前安全状态后的实际能力。

### 当前 M3-A 门禁

当前正式能力事实固定使用：

```text
DesktopAutomationUnattendedGate.M3AProvisionalClosed
Reason = M2RealDualMonitorAcceptancePending
```

因此即使：

```text
RequestedMode = Automatic
```

当前也只能得到：

```text
EffectiveMode = Suggestion
CanAttemptUnattendedExecution = false
BlockReason = UnattendedWriteGateClosed
```

05A 定义的 `ValidatedOpen` 只供自动测试，以及未来真实 M2 双屏验收完成后的 composition root 使用。这个 gate 对象本身不授予 Writer 能力。

## Manual

```text
Requested = Manual
→ Effective = Manual
→ background observation policy = off
→ suggestion presentation policy = off
→ unattended execution = off
```

05A 只计算这些能力位，不主动停止现有 monitoring session。

实际把 mode 连接到 monitoring / tray 属于后续 integration。

## Suggestion

```text
Requested = Suggestion
→ Effective = Suggestion
→ background observation allowed
→ suggestion presentation allowed
→ unattended execution forbidden
```

这是 DesktopAgent 当前默认模式。

## Automatic

只有全部满足时才可能：

```text
Requested = Automatic
AND unattended environment gate open
AND not Paused
AND not in Cooldown
AND Fuse not tripped
→ Effective = Automatic
```

任何 blocker 存在都降级到 Suggestion，而不是强行执行。

## Pause

Pause 只暂停“手”，不暂停“眼睛”：

```text
Automatic + Paused
→ Effective = Suggestion
→ observation can continue
→ suggestion can continue
→ unattended execution blocked
```

支持：

- indefinite pause；
- timed pause。

Timed pause 不需要 timer。Snapshot 每次根据 `TimeProvider.GetUtcNow()` 判断是否仍 active，到点自然恢复能力计算。

## Execution Cooldown

Settings：

```text
ExecutionCooldown
```

每次未来的 execution attempt，无论成功或失败，都进入 cooldown：

```text
RecordExecutionSuccess
or
RecordExecutionFailure
→ CooldownUntilUtc = now + ExecutionCooldown
```

原因：失败同样不应形成高速连续重试。

如果 cooldown 设置为 0，则不制造虚假的 cooldown blocker。

05A 没有真实 execution caller，这些 transition 目前只有测试使用。

## Consecutive-failure Fuse

Settings：

```text
FuseFailureThreshold > 0
```

连续失败：

```text
Failure #1
Failure #2
...
Failure #N == threshold
→ FuseTripped
→ Effective Automatic 降级 Suggestion
```

成功会清除“尚未熔断”的连续失败计数。

但 Fuse 一旦真正 trip：

```text
late / manual success
→ 不自动解除 Fuse
```

必须：

```text
ResetFuse()
```

显式恢复。

这样避免迟到的成功事件或旁路人工操作无意间重新打开自动化能力。

## Block Reasons

05A 返回确定顺序的 unattended blocker：

- RequestedManualMode
- RequestedSuggestionMode
- UnattendedWriteGateClosed
- Paused
- CooldownActive
- FuseTripped

例如当前 M3-A 下：

```text
Requested Automatic
+ Paused
+ Cooldown
+ Fuse
+ M2 gate closed
```

会同时得到：

```text
UnattendedWriteGateClosed
Paused
CooldownActive
FuseTripped
```

不只返回“第一个失败原因”，方便以后 diagnostics / tray / history 展示完整状态。

## Runtime Snapshot

Snapshot 包含：

- Revision；
- RequestedMode / EffectiveMode；
- unattended environment gate；
- Pause state / reason / deadline；
- Cooldown state / deadline；
- consecutive failure count；
- Fuse state / threshold；
- last execution outcome / timestamp；
- deterministic block reasons；
- `CanObserveInBackground`；
- `CanPresentSuggestions`；
- `CanAttemptUnattendedExecution`。

Revision 只跟显式状态 mutation 变化；定时 pause / cooldown 的时间自然到期不会为了读 Snapshot 人为制造 revision。

## 当前不接生产执行链

05A 特别不做：

```text
Suggestion
→ automation policy
→ Writer
```

当前没有任何 production caller 调用：

- `RecordExecutionSuccess()`
- `RecordExecutionFailure()`

它们只是未来 M3-B executor 必须遵守的状态 transition contract。

## 自动测试

覆盖：

- default Suggestion + provisional gate；
- requested Automatic 在 M3-A gate 下自动降级；
- validated-open 环境下 Automatic eligibility；
- Manual 关闭 background/suggestion capability bits；
- Pause / Resume；
- timed Pause expiry；
- failure cooldown / expiry；
- success resets untripped failure count；
- fuse threshold / explicit reset；
- success does not auto-reset tripped fuse；
- multiple blockers deterministic order；
- zero cooldown；
- invalid settings / pause args fail closed。

## 不在范围

- mode / pause 持久化；
- tray mode selector；
- candidate risk gate；
- maximum move / step limits；
- unit-time execution rate limit；
- automation history；
- unattended executor；
- Writer。

## 下一步

M3-05B 可以做 Candidate Risk Policy：

```text
Active Suggestion / M2 Candidate evidence
+ max move count
+ max execution step count
+ staging / topology / assignment risk
→ EligibleForAutomatic / FallBackToSuggestion
```

它仍只输出决策，不调用 Writer。

真实 M2 双屏验收完成以前，composition root 仍必须使用 `M3AProvisionalClosed`。
