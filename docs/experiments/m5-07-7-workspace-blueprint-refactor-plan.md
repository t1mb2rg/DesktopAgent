# M5-07.7 Workspace Blueprint 重构计划

- 状态：Architecture Review 后待实施
- 日期：2026-08-27
- 依据：ADR-0005《AI Workspace Blueprint 与 Explorer 原生编译》
- 当前 PR：#107
- 当前分支：`agent/m5-07-7-direct-layout-apply`

## 目标

把当前 M5-07.7 的 AI Logical Workspace 中段从：

```text
Fresh Observation
→ Semantic Understanding
→ Logical Workspace Projection
→ AI Exact Logical Planner
→ Desired exact-cell Plan
→ Frozen Preview
```

重构为：

```text
Conversation
→ Semantic Understanding
→ explicit DesignRequested
→ AI Workspace Blueprint Revision
→ Explorer Geometry Adapter
→ Deterministic Workspace Compiler
→ Semantic Equivalence
→ Exact Workspace Plan + Realization Certificate
→ Frozen Workspace Preview
```

同时保持既有 Windows 安全基础设施和 legacy single-display Layout 产品能力。

## 基线

### 已通过真实机 / 静态 Gate 的观测基线

`1b4c91ebd97d3095593bc44e1428d0a074db5839`

该基线已证明/暴露：

- 3-display Fresh Observation 可用；
- 多显示器 Explorer Grid Phase 需要真实适配；
- boundary/tail icon 不能简单按理想网格解释；
- SystemItem 不可移动 invariant 必须保持；
- 只读跨屏 Frozen Preview 可工作；
- Preview 大量迁移线需要限流和分区优先可读性；
- successful Frozen Preview 生命周期不能被 deep resident 静默释放；
- 130-item exact Planner 存在明显可扩展性压力；
- 第二阶段 exact Planner 会重新发明 Zone 语义。

### Architecture Review 前实验性补丁

`1b4c91e → 10b57bc` 共 5 个提交：

- semantic planning intent；
- semantic continuity gate；
- 对 exact-cell Planner contract 的继续补强。

这些提交仅作为问题证据，不作为 ADR-0005 的实现基础。后续应整体撤销或重写方向。

## 分类

### KEEP

这些能力继续作为新架构基础：

- `WindowsDesktopObservationSource`；
- `NativeDisplayTopologyReader`；
- `DesktopShellIconReader`；
- `DesktopShellIconPositioningCapabilitiesReader`；
- `DesktopShellIconPositionWriter` / primitive writer boundary；
- Safety Snapshot primitives；
- Persistence primitives；
- independent verification；
- unresolved recovery fail-closed 原则；
- SystemItem 不可移动 invariant；
- 多屏 Topology Canvas；
- Preview 大量迁移线限流和聚焦显示逻辑；
- Frozen Preview 生命周期保护原则。

### ADAPT / 下沉

这些不是坏代码，但职责应移动：

- `DesktopExplorerGridPhaseResolver`
  - 从 Logical Workspace projection 前置条件下沉到 Explorer Geometry Adapter；
- spacing / work-area / negative virtual coordinates；
- fixed obstacle / occupied slot 识别；
- Preview UI
  - 从 `ObservedLogicalWorkspace + DesiredExactPlan` 改为 `Blueprint + ExactPlan + Certificate`；
- 现有 semantic understanding Provider
  - 去除 Zone/layout authority，保留 Category / Item→Category / uncertainty。

### REWRITE

以下模块实现的是 ADR-0004 exact-cell 领域模型，需要重写而不是继续补丁：

- `DesktopObservedLogicalWorkspace`；
- `DesktopLogicalWorkspaceProjector`；
- `DesktopLogicalWorkspaceSemanticOverlay`；
- `DesktopLogicalWorkspaceSemanticPlanningIntent`；
- `DesktopLogicalWorkspacePlanningContract`；
- `OpenAiCompatibleDesktopLogicalWorkspacePlanner`；
- `DesktopLogicalWorkspacePlanningSessionCoordinator`；
- `DesktopDesiredWorkspacePlan` 作为 AI 直接 exact-cell 输出的职责；
- `DesktopDesiredWorkspacePlanValidator` 的职责边界；
- `DesktopLogicalWorkspaceFrozenPreview` 数据模型；
- `AiDesktopLogicalWorkspacePlanningService` 产品状态机；
- 新 AI Workspace 的 Candidate / Execution / Recovery scope。

### LEGACY / COMPATIBILITY

保留，但新架构不得继续依赖：

- `DesktopUnderstandingLayoutDraftCompiler`；
- `DesktopUnderstandingMultiDisplayWorkspaceCompiler`；
- per-display `DesktopLayoutEditDraft`；
- legacy single-display Preview / Direct Apply / Recovery 产品路径；
- `TargetDisplayLayoutExecutionCoordinator` 的 per-display 业务 scope。

可以复用其底层 mechanics，但不能把新 Workspace Blueprint 编译回多份 legacy Draft 来获得执行能力。

## 分阶段实施

### Phase 0：冻结 Architecture Baseline

目标：停止在 exact-cell Planner 上继续贴补丁。

动作：

1. ADR-0004 标记 Superseded；
2. ADR-0005 Accepted；
3. 本文档作为 M5-07.7 重构施工顺序；
4. `1b4c91e → 10b57bc` 标记为 experimental architecture patch；
5. 在新模型可替代前不删除 legacy product path；
6. 暂停真实 Writer Gate。

Gate：文档审计完成，无业务代码改动。

### Phase 1：拆 Semantic Understanding 与 Workspace Design

目标：先解决 authority 重叠，不碰 Windows 几何。

新增/重构概念：

```text
DesktopSemanticSnapshot
DesktopSemanticUnderstanding
DesktopSemanticCategory
DesktopSemanticItemAssignment
DesktopSemanticAmbiguity
```

要求：

- Understanding 不输出 Zone；
- Understanding 不输出 target display；
- Understanding 不输出 Keep/Move policy；
- design-critical ambiguity 阻塞 DesignRequested；
- minor uncertainty 可进入 Inbox/Uncertain。

同时建立产品 authority state：

```text
Conversation
Understanding
DesignRequested
```

Gate：

- 纯聊天/理解绝不创建 Blueprint、Preview 或 Writer authority；
- 原有 AI semantic result 不再携带布局设计事实。

### Phase 2：建立 Workspace Blueprint v1

目标：让 AI 真正设计桌面，但不触碰 Explorer Cell。

建议模型：

```text
WorkspaceBlueprint
WorkspaceBlueprintRevision
BlueprintDisplayRole
BlueprintZone
BlueprintReservedRegion
BlueprintCategoryRule
BlueprintItemOverride
BlueprintAnchor
```

Blueprint 表达：

- 整体多屏 Workspace；
- display role/ref；
- Zone 相对区域/比例；
- Zone topology relations；
- Category → Zone；
- Item Override；
- Keep / Exclude；
- Reserved Region；
- anchor / priority。

Provider 输入默认使用：

- category summary；
- category item count；
- workspace relative topology；
- user guidance；
- design-critical exceptions。

默认不再次发送完整 item list。

Gate：

- 130-item 桌面下 Workspace Designer 输出规模不随 item count 线性膨胀；
- AI 输出中不存在 exact cell / pixel；
- 同一 Blueprint revision 不可变。

### Phase 3：Explorer Geometry Adapter

目标：把 Windows 几何全部关进本地基础设施层。

建议模型：

```text
ExplorerWorkspaceGeometry
ExplorerDisplayGeometry
ExplorerNativeSlot
ExplorerFixedObstacle
ExplorerGeometryFingerprint
```

Adapter 负责：

- WorkArea；
- spacing；
- Grid Phase；
- negative coordinates；
- taskbar/work-area offset；
- fixed / non-movable obstacles；
- Explorer 实际可安全使用的 slot；
- 无法稳定推导时 fail-closed。

重要：

- 当前现实 item 不必先拥有规则 `CurrentCell`；
- geometry failure 只阻塞 Compile，不阻塞 Semantic Understanding。

Gate：

- 真实 free-placement / boundary icon 不再导致 semantic path 失败；
- Adapter 对当前三屏机器能给出稳定 Explorer-native slot surface。

### Phase 4：Deterministic Workspace Compiler

目标：本地只施工，不设计。

建议输出：

```text
CompiledWorkspacePlan
CompiledWorkspaceZone
CompiledWorkspaceItem
CompileDiagnostic
```

Compiler 允许：

- ratio → nearest legal Explorer geometry；
- Category→Zone 展开；
- deterministic ordering / packing；
- fixed obstacle avoidance；
- Blueprint anchor realization。

Compiler 禁止：

- reclassify；
- change display；
- change Zone relation；
- semantic resize；
- move item to another Zone；
- invent Keep/Exclude/Overflow Zone。

失败返回结构化 diagnostics，不做修复。

Gate：

- same Blueprint + semantic snapshot + geometry = same plan；
- capacity failure 必须 fail，不允许本地自动扩区。

### Phase 5：Semantic Equivalence + Realization Certificate

目标：证明“Windows 落成仍是 AI 设计”。

新增：

```text
WorkspaceSemanticEquivalenceChecker
WorkspaceRealizationCertificate
```

Hard semantics：

- Zone existence；
- display ownership；
- topology relation；
- Category→Zone；
- Item Override；
- Keep/Exclude；
- Reserved Region；
- explicit anchor semantics。

Quantizable semantics：

- 只允许 nearest unavoidable Explorer discretization。

Gate：

- Compiler 不能用 quantization 名义扩大容量；
- 每次成功 Compile 都有可审计 Certificate；
- 不等价结果绝不进入 Preview。

### Phase 6：Bounded AI Redesign Loop

目标：Compiler 失败后把事实退回 AI，而不是本地改图。

流程：

```text
Blueprint Revision N
→ Compile FAIL
→ Provider-safe diagnostics
→ AI Designer
→ Blueprint Revision N+1
```

要求：

- diagnostics 只给事实，不给设计答案；
- revision immutable；
- retry bounded；
- 超预算进入 `DesignUnresolved`。

Gate：

- 真实 capacity conflict 能形成新 revision；
- Compiler authority 不扩大。

### Phase 7：Frozen Workspace Preview v2

目标：用户真正确认 Blueprint + 当前精确 realization。

Preview 绑定：

```text
BlueprintRevisionId
CompiledPlanId
RealizationCertificateId
SourceObservationId / GeometryFingerprint
```

保留/适配现有：

- multi-display topology canvas；
- per-display focus；
- Zone / Reserved Region overlay；
- migration summary；
- limited migration lines；
- preview retention lifecycle。

Gate：

- 用户看到的结果与 Exact Plan 一致；
- Frozen Preview 后 AI/Compiler 不能静默改当前对象；
- 修改设计产生新 revision + new preview。

### Phase 8：Fresh Check 与 Re-preview

目标：把 stale 语义定清楚。

规则：

- fresh facts 与 confirmed exact realization 一致：允许继续；
- same Blueprint faithful recompile 后 Exact Plan 有任何用户可见变化：必须重新 Preview；
- Blueprint 无法继续 faithful realization：回 AI Design；
- 不允许静默 fresh replan 后直接 Writer。

Gate：

- stale slot / topology / obstacle 场景均 0 Writer；
- changed exact plan 强制 re-preview。

### Phase 9：Workspace-level Execution / Recovery

目标：最后才接真实 Writer。

新 Workspace execution aggregate 至少绑定：

- confirmed Blueprint revision；
- confirmed exact plan；
- source/fresh observation；
- intended final state；
- safety snapshot；
- recovery checkpoint。

执行仍使用 primitive Writer，可复用现有：

- Shell read/write primitives；
- STA execution mechanics；
- snapshot persistence；
- independent verification；
- fail-closed recovery philosophy。

不复用 per-display transaction identity。

Recovery：

- workspace-scoped；
- fresh fact driven；
- 不猜过去副作用；
- 不重新设计 Blueprint。

Gate：

- 跨屏 partial execution crash 可在 Workspace scope 下描述；
- unresolved state 阻塞新的 Workspace Writer；
- 真实 Physical Apply 重新从 Preview/Confirm/Fresh/Checkpoint 全链验证。

## 建议的第一刀

代码重构不要从 Writer 开始，也不要从现有 `DesktopDesiredWorkspacePlanValidator` 继续扩展。

第一阶段只做：

```text
Semantic Understanding authority cleanup
+
Workspace Blueprint v1 domain contracts
+
Provider contract tests
```

此阶段：

- 0 Writer；
- 0 Legacy Apply 变化；
- 0 Recovery schema 变化；
- 不需要真实移动桌面；
- 可以快速建立新架构的第一个稳定纵切面。

## 删除时机

以下旧 Logical Workspace exact-cell 类型不要立即删除。先让 Blueprint path 完成到 Frozen Preview v2，并通过静态与真实只读 Gate，再移除/迁移：

- `DesktopObservedLogicalWorkspace`；
- `DesktopDesiredWorkspacePlan`；
- `OpenAiCompatibleDesktopLogicalWorkspacePlanner`；
- `DesktopLogicalWorkspacePlanningSessionCoordinator`；
- 当前 Frozen Preview projector。

这能避免一次性破坏当前 PR 中仍可运行的只读产品证据。

## 验收原则

每一 Phase 都必须保持：

- Build PASS；
- full tests PASS；
- Writer authority 不向 WinUI/AI 泄漏；
- 新架构失败时 fail-closed；
- 不通过“本地自动修复”掩盖 Blueprint 不可实现；
- 真实 Windows Gate 只在对应层成熟后逐级开启。

最终 M5-07.7 的目标不是“让当前 exact Planner 勉强通过 Physical Gate”，而是建立：

> AI 真正设计整个桌面，Windows Explorer 决定真实几何，本地确定性代码忠实施工，用户只执行自己实际看过并确认的结果。
