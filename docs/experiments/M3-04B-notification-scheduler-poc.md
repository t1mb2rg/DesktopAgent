# M3-04B：Deferred Notification Scheduler + PoC Surface

## 目标

在 M3-04A 已建立 notification policy / dedup / cooldown / sink port 后，把 deferred 结果真正调度起来，并在 PoC 中提供一个不依赖 Windows Toast 的可观察通知 surface：

```text
Suggestion lifecycle
→ 04A dispatcher
→ 04B scheduler
→ PoC notification sink
→ Open / Ignore interaction
→ STOP
```

本阶段仍然没有 Candidate execution、Writer 或 ExecutionCoordinator。

## Scheduler 原则

### 只保存最新 lifecycle

scheduler 不保存“待发送的旧 payload”。

假设：

```text
S1 已通知 @ T0
S2 @ T+2s → cooldown / deferred
S3 @ T+8s → 取代 S2
```

scheduler 保存的是最新 lifecycle S3。到 cooldown deadline：

```text
wake
→ 重新 Evaluate 当前最新 lifecycle
→ 只有仍 Active 的最新 Suggestion 才有资格 Publish
```

因此不会在 deadline 到来时机械发送已经过期的 S2。

### generation gate

每次新 lifecycle 进入 scheduler：

1. 更新 latest lifecycle；
2. generation +1；
3. 取消上一条 deferred delay；
4. 对当前 generation reconcile。

旧 timer 醒来时 generation 不匹配会直接退出。

### timer cancellation 只取消等待阶段

deferred timer 的 cancellation token 只用于 Delay。

一旦 deadline 到达并通过 generation gate，实际：

```text
Withdraw / Publish / acknowledge
```

不再使用旧 timer token。

这样避免：

```text
平台 Publish 已产生副作用
→ cancellation 恰好发生
→ acknowledge 未执行
→ policy 误以为从未通知
```

通知不是 M2 Writer，但仍保持“副作用与内部状态不要制造人为半状态”的原则。

## Stale-safe Ignore

Notification interaction 会携带 SuggestionId。

新增 lifecycle 原子操作：

```text
DismissCurrent(expectedSuggestionId)
```

行为：

```text
旧 notification = S1
当前 lifecycle = S2
Ignore(S1)
→ Dismissed=false
→ S2 保持不变
```

只有 expected id 与当前 SuggestionId 完全一致时，才会退休为 UserDismissed。

## PoC notification sink

`M3DiagnosticsNotificationSink` 只维护内存 surface：

- 当前 notification payload；
- surface revision；
- LastEvent；
- 模拟 OpenSuggestion；
- 模拟 IgnoreSuggestion。

它不调用 Windows Toast，也拿不到 Candidate / ExecutionPlan / Writer。

## PoC bridge

`M3SuggestionNotificationCoordinator`：

- 订阅 `M3SuggestionMonitoringCoordinator.SuggestionProcessed`；
- 把 lifecycle 送进 scheduler；
- 接收 sink interaction；
- OpenSuggestion 只发出“打开 UI”请求；
- IgnoreSuggestion 使用 expected SuggestionId 原子 dismiss；
- stale Ignore 后仍重新 reconcile 当前 lifecycle，以清理可能残留的旧 surface。

03D coordinator 本体不为 Notification 重构。

对于绑定变化、Reset baseline、诊断窗里的手动忽略，04B 在已有 WPF click handler 之后追加一次 lifecycle reconcile，保持 PoC 范围内的完整 notification 对账。

## Diagnostics UI

M3 Diagnostics Window 新增 PoC Notification 区：

- 当前内存通知摘要；
- scheduler 状态；
- cooldown deadline；
- 模拟打开；
- 模拟忽略。

PoC cooldown 暂设 30 秒，纯粹为了 Windows 实机人工验收方便，不冻结为产品默认值。

Open 只激活诊断窗口；Ignore 只 dismiss Suggestion。没有执行整理按钮。

## 自动测试

覆盖：

- S2 deferred 后 S3 替换，deadline 只 publish 最新 S3；
- Suspended lifecycle 取消 deferred retry；
- Dispose 取消 deferred delay；
- stale SuggestionId 不得 dismiss replacement；
- PoC sink 只撤回匹配的 current notification；
- PoC sink 可模拟晚到的 stale interaction；
- Diagnostics Window / Notification coordinator 依赖图不暴露 Writer。

## 不在范围

- Windows App Notification；
- system tray surface；
- notification persistence / startup replay；
- OS notification activation registration；
- user-confirmed execution handoff；
- unattended Writer。

## 下一步

04B 通过 CI 与 Windows 实机 PoC 后，再进入 M3-04C，比较并选择：

1. Windows App Notification；
2. tray surface / balloon；
3. 两者组合。

无论选择哪一种，平台层都只实现 04A sink contract，不获得 M2 Candidate 或 Writer。
