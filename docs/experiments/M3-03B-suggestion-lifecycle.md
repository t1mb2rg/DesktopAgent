# M3-03B：Suggestion Lifecycle

## 目标

在 M3-03A 能生成 Candidate-backed Suggestion 后，定义建议在后台持续观察中的生命周期，避免把旧 Observation 上的建议长期展示成“仍然新鲜”。

本阶段只管理状态，不执行 Candidate，不发送系统通知，也不接 Writer。

## 状态

```text
None
Active
Suspended
```

### Active

存在一份当前可展示的 Suggestion。

这里的“Active”只表示建议内容可作为当前 UI/Notification 的建议展示，不表示 Candidate 可以直接执行。用户真正应用时仍必须 fresh rebuild M2 Candidate。

### Suspended

保留 Suggestion 内容，但当前无法确认桌面仍处于稳定、完整可观察状态。

暂停原因：

- ObservationUnstable；
- ObservationUnavailable。

Suspended 时不得将建议呈现为“当前可直接确认”的新鲜建议。

### None

没有当前建议。

## 关键语义

### 新 ObservationId 本身不会让 Suggestion 过期

Periodic fallback 会不断生成新的 ObservationId。

如果新的 fresh stable observation 与最近 stable baseline 的事实没有变化（Diagnostics = NoChanges），旧 Suggestion 不应每个周期被销毁。

因此生命周期基于“事实是否改变”，而不是基于 ObservationId 是否改变。

### Unstable / Unavailable 先 Suspend

当当前 observation 无法建立或两次采样不稳定时：

```text
Active
→ Suspended
```

不立即删除 Suggestion，因为 baseline 尚未推进，当前事实只是“暂时不能证明”。

后续如果 fresh stable observation 明确得到 NoChanges：

```text
Suspended
→ Active
```

如果后续检测到真实 State Diff：

```text
Suspended
→ retired / None
```

### 真实桌面变化立即退休旧建议

`MarkDesktopChanged(newStableObservationId)` 会把 Active/Suspended Suggestion 退休为 `DesktopChanged`。

新的 Suggestion 必须随后重新经过 03A Builder 生成。

如果新一轮 Builder 返回 NoActionNeeded / CandidateRejected / TemplateMissing 等结果，则保持 None，不允许旧 Suggestion 复活。

### Baseline 重新建立会退休旧建议

显式 Reset Baseline 或其他原因导致下一份 stable observation 重新建立 baseline 时，旧 Suggestion 失去连续比较链的证据基础。

因此：

```text
Active / Suspended
→ BaselineReestablished
→ retired / None
```

这与“周期产生一个新的 ObservationId”不同。普通 `NoChanges` 不会退休建议；只有 baseline 本身被重新建立才会。

### Binding 变化立即退休

用户切换 TemplateId / SelectedTarget 等后台绑定时，旧 Suggestion 不再属于当前配置，退休为 `BindingChanged`。

### 用户可主动 Dismiss

Dismiss 只影响建议展示生命周期，不改变模板、规则、baseline 或桌面事实。

## Retirement diagnostics

保留最近一次退休信息：

- retired Suggestion；
- Reason；
- RetiredAtUtc；
- optional NewStableObservationId。

Reason：

- DesktopChanged；
- BindingChanged；
- UserDismissed；
- ReplacedByNewSuggestion；
- BaselineReestablished。

当前只保留进程内最近一次退休记录，不进入 SQLite history。

## Revision

Lifecycle 维护单调递增 Revision。

只有状态实际变化才递增；重复 Suspend 同一原因、None 状态下 ConfirmNoChanges 等 no-op 不递增。

后续 UI/notification 可以使用 Revision 避免重复渲染/重复通知。

## 自动测试

覆盖：

- Publish -> Active；
- Suspend -> Suspended；
- NoChanges -> 恢复同一 Suggestion；
- DesktopChanged 退休 Active；
- DesktopChanged 退休 Suspended；
- BaselineReestablished 退休当前 Suggestion；
- Publish 新 Suggestion 替换旧 Suggestion；
- BindingChanged；
- UserDismissed；
- 无 Suggestion 时 Suspend / ConfirmNoChanges 为 no-op。

## 下一步

M3-03C 建议把 monitoring diagnostics 与 03A Builder + 03B Lifecycle 串成只读 Suggestion pipeline：

```text
DiagnosticsResult
→ NoChanges / Unstable / Unavailable lifecycle handling
→ ChangesDetected: retire old
→ read explicit binding
→ 03A Build
→ publish new Suggestion if any
→ UI/Notification sink
```

仍然 STOP before Writer。
