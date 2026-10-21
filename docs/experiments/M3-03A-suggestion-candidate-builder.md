# M3-03A：Suggestion Candidate Builder

## 目标

在 M3 已有 stable Observation / State Diff 基础上，首次生成真正的“建议候选”，但仍然停在只读边界：

```text
stable Observation
+ State Diff
+ explicit Suggestion Binding
→ fresh template + rule set read
→ M2 Rule-aware Preview Planner
→ M2 Execution Candidate Planner
→ DesktopLayoutSuggestion
→ STOP
```

Suggestion 不调用 Writer，不调用 ExecutionCoordinator，也不构成执行授权。

## 为什么需要显式 Binding

Roadmap 同时要求：

- 自动选择模板和规则；
- 用户绑定模板。

当前持久化层只有模板/规则的 CRUD，没有“后台当前应该使用哪个模板”的既有事实。

M3-03A 不采用以下隐式策略：

- 取模板列表第一项；
- 取最近修改模板；
- 猜当前 WPF ComboBox 选择；
- 根据桌面状态自行挑模板。

新增 `DesktopLayoutSuggestionBinding`：

```text
TemplateId
SelectedTargetDeviceName?
```

`TemplateId` 必须显式提供。

如果模板使用 `SelectedTarget`，还必须显式提供 `SelectedTargetDeviceName`；PrimaryDisplay / ExactDeviceName 继续完全沿用 M2 模板自身绑定语义。

## Observation / Diff 一致性

Builder 要求：

```text
changeSet.CurrentObservationId == currentObservation.ObservationId
```

否则返回 `ObservationMismatch`，禁止把不同时间点的 Diff 与 Observation 拼成建议。

空 Diff 直接返回 `NoChanges`，且不会读取模板/规则。

## 复用 M2 Planner

M3 不复制布局算法。

Builder 依次复用：

1. `DesktopLayoutTargetDisplayResolver`；
2. `DesktopLayoutRuleAwarePreviewPlanner`；
3. `DesktopLayoutRuleAwareExecutionCandidatePlanner`。

因此以下既有安全语义继续生效：

- Auto Arrange；
- IconSpacing；
- target display；
- StableId / metadata / position stale checks；
- Fixed / Exclude / Pin；
- Unassigned；
- restore blocking；
- dependency / cycle / staging；
- candidate executability。

## Ordinal / 输入顺序

M2 手动 Preview 当前通过 `DesktopLayoutSnapshotFactory` 把 fresh Shell read 顺序映射为连续 Ordinal，并将该顺序输入 Rule-aware Preview Planner。

M3-03A 保持相同语义：使用 current stable Observation 中保存的 fresh Shell icon 顺序，不私自按 StableId 重排。

原因：如果 Suggestion 使用一套新排序、用户确认后的 M2 fresh rebuild 使用另一套排序，可能在事实未变化时产生不同 slot assignment。

是否应在未来把 Placement 顺序改为更强的显式确定性规则，应作为独立兼容性审计，不在本阶段顺带修改 M2。

## Suggestion 状态

- `Suggested`：M2 Candidate 可执行且非 NoOp；
- `NoChanges`：State Diff 为空；
- `NoActionNeeded`：桌面确有变化，但当前模板已经满足，M2 Candidate 为 NoOp；
- `ObservationMismatch`；
- `TemplateMissing` / `TemplateLoadFailed`；
- `RuleSetMissing` / `RuleSetLoadFailed`；
- `BindingIncomplete`；
- `PreviewRejected`；
- `CandidateRejected`。

## Suggestion 不是执行授权

`DesktopLayoutSuggestion` 保存：

- SuggestionId / CreatedAtUtc；
- SourceObservationId / PreviousObservationId；
- explicit Binding；
- SourceChangeSet；
- M2 `DesktopLayoutRuleAwareExecutionCandidate`。

Candidate 只是“在 SourceObservation 对应事实下，当时的 M2 计划证据”。

用户未来点击应用时，不允许把该 Candidate 直接送进 Writer。正式路径仍必须：

```text
user confirms suggestion
→ fresh Shell reread
→ fresh template/rules reread
→ rebuild Preview + Candidate
→ semantic / scope / recovery gates
→ existing M2 Coordinator
→ Writer
```

M3-A provisional gate 当前仍然禁止 unattended real Writer。

## 自动测试

覆盖：

- Builder 构造依赖不含 Writer；
- MoveRequired → Suggested + M2 Candidate；
- changed-but-already-satisfied → NoActionNeeded；
- empty Diff 不读取 stores；
- ObservationId mismatch；
- template missing；
- rule set missing；
- SelectedTarget 缺设备绑定；
- SelectedTarget 显式设备成功；
- Auto Arrange 由 M2 Candidate Planner 拒绝，不提升为 Suggestion；
- ExactDeviceName 目标不存在。

## 不在范围

- Suggestion Binding 的 SQLite 持久化；
- 自动模板选择策略；
- Suggestion lifecycle / supersede / expire；
- monitoring session 自动调用 Builder；
- notification；
- 用户点击建议后的 fresh rebuild 执行入口；
- unattended Writer。

下一阶段建议 M3-03B 先做 Suggestion lifecycle（current / superseded / stale）和显式 binding provider，再接 UI/notification。
