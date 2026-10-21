# M3-04A：Notification Policy / Dedup / Cooldown / Sink Port

## 目标

在 M3-03D 已经能产生并维护 Active / Suspended Suggestion 后，先建立平台无关的通知策略，不直接接 Windows Toast：

```text
Suggestion lifecycle
→ Notification policy
→ Withdraw / Publish / Deferred plan
→ Notification dispatcher
→ IDesktopSuggestionNotificationSink
→ STOP
```

本阶段不读取 State Diff、不读取 Shell、不持有 M2 Candidate，也不接 Writer / ExecutionCoordinator。

## 核心原则

### Notification 只消费 Suggestion lifecycle

Notification 不订阅：

- FileSystem / periodic hint；
- 每轮 monitoring；
- State Diff；
- M2 Candidate execution state。

通知唯一事实输入是 M3-03B/03C 已经解释后的 Suggestion lifecycle。

因此 periodic fallback 每 15 秒产生新的 ObservationId / NoChanges 不会直接触发重复通知。

## 只读 Notification Payload

新增 `DesktopSuggestionNotificationPayload`：

- SuggestionId；
- LifecycleRevision；
- SuggestionCreatedAtUtc；
- TemplateId；
- TargetDeviceName；
- MoveRequiredCount；
- ExecutionStepCount；
- UsesTemporaryStaging；
- SourceObservationId。

刻意不包含：

- `DesktopLayoutRuleAwareExecutionCandidate`；
- RestorePlan / ExecutionPlan；
- Writer；
- ExecutionCoordinator。

平台通知层因此从类型上拿不到可执行计划。

## Notification action contract

定义只读交互：

- `OpenSuggestion`；
- `IgnoreSuggestion`。

Interaction 只携带 SuggestionId + Action，不是执行授权。

未来：

- Open 只应打开 Suggestion UI；
- Ignore 只应 dismiss Suggestion lifecycle；
- 真正“应用建议”仍必须从 UI 重新 fresh reread / rebuild M2 Candidate。

## Sink port

`IDesktopSuggestionNotificationSink`：

```text
PublishAsync(payload)
WithdrawAsync(suggestionId)
InteractionReceived
```

没有 Execute / Apply API。

04B 可以实现 Windows App Notification、tray 或其他 surface，而不改变 policy。

## Policy 状态

Notification policy 仅维护：

- `SurfacedSuggestionId`：当前平台通知 surface 上仍认为存在的 Suggestion；
- `LastPublishedSuggestionId`；
- `LastPublishedAtUtc`。

策略只在 sink 成功后通过 `AcknowledgePublished / AcknowledgeWithdrawn` 推进状态。

如果平台 API 失败，不会把内部状态假装成已经成功。

## Dedup

### 首次 Active

```text
Active S1
→ Publish S1
```

### 同一 Suggestion 重复 lifecycle 事件

```text
Active S1
→ AlreadyPublished
→ no publish
```

### Suspended

```text
Active S1 (surfaced)
→ Suspended S1
→ Withdraw S1
```

### 恢复同一 Suggestion

```text
Suspended S1
→ NoChanges
→ Active S1
→ AlreadyPublished
→ 不重新弹
```

因此短暂 Unstable / Unavailable 不会造成同一建议反复弹窗。

## 新 Suggestion 与旧 notification

当前 surfaced S1，生命周期已变为 Active S2：

```text
Withdraw S1
→ 根据 cooldown 决定 Publish S2 或 Deferred S2
```

Withdraw 与 Publish 可以出现在同一 plan 中，dispatcher 固定先撤旧、再发新。

如果撤旧失败，dispatcher 不继续发新，避免 Action Center 同时残留两个互相矛盾的 Suggestion。

## Cooldown

Settings 使用 `MinimumPublishInterval`，具体时长不在 Core 冻结。

假设：

```text
S1 published @ T0
MinimumPublishInterval = 10 min
```

T0+2min 出现 S2：

```text
Withdraw S1
DeferredUntil = T0+10min
```

T0+4min S2 又被 S3 替换：

```text
仍 DeferredUntil = T0+10min
```

到 T0+10min 再 Evaluate 当前 lifecycle 时，只会发布当时仍 Active 的最新 Suggestion，例如 S3。

04A 只返回 `DeferredUntilUtc`，不启动 timer。04B 再负责到点重试/调度。

## Dispatcher

`DesktopSuggestionNotificationDispatcher` 使用自己的 single-flight gate：

```text
Evaluate lifecycle
→ Withdraw old if required
→ acknowledge withdraw only after sink success
→ Publish new if eligible
→ acknowledge publish only after sink success
```

并发 Reconcile 同一 lifecycle 时，第一轮成功 Publish 后，第二轮重新 Evaluate 会得到 AlreadyPublished，不会重复发布。

## Sink failure

### Withdraw failure

- 返回 `SinkFailed / Withdraw`；
- 不 acknowledge withdraw；
- 不继续 publish replacement；
- 下一轮重试。

### Publish failure

- 返回 `SinkFailed / Publish`；
- 不 acknowledge publish；
- 下一轮仍有资格重试。

服务 cancellation 不包装为 SinkFailed，继续向上传播。

## 自动测试

覆盖：

- first Active publish once；
- same Suggestion dedup；
- Suspended withdraw；
- resume same Suggestion no re-notify；
- multiple new Suggestions inside cooldown，latest current wins at eligibility；
- payload / sink contract 不暴露 Candidate / Writer；
- withdraw failure blocks replacement publish；
- publish failure can retry；
- concurrent reconcile same snapshot publishes once。

## 不在范围

- Windows App Notification 实现；
- deferred timer / scheduler；
- notification persistence；
- startup notification replay；
- Open / Ignore 的实际 UI routing；
- tray UX；
- user-confirmed execution handoff；
- unattended Writer。

## 下一步

M3-04B 建议先实现 notification scheduling coordinator + PoC sink：

```text
SuggestionProcessed / lifecycle change
→ 04A dispatcher
→ DeferredUntil scheduler
→ platform-neutral PoC notification sink
```

验证去重 / cooldown / interaction routing 后，再在 04C 选择 Windows App Notification 或 tray surface。
