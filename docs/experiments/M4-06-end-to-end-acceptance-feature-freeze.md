# M4-06 End-to-End Acceptance + v1.0 Core Feature Freeze

## 目的

M4-06 不继续扩张 DesktopAgent 的核心功能范围。

本阶段只做两件事：

1. 把 M4-00～M4-05 已完成的 Intelligence / Provider / First-run / Policy / Profile / RuleDraft / Content Analysis 串成可供 M5 UI 使用的 UI-agnostic 日常分类工作流；
2. 对 M4 故障与安全边界做端到端验收，全部通过后进入 `v1.0 CORE FEATURE FREEZE`。

M4-06 不获得新的桌面写权限。

## 日常分类工作流

`DesktopClassificationWorkflow` 只负责已有 M4 组件的编排：

```text
DesktopItemFacts + active Template + logical Zones
→ persisted Rules
→ local Policy resolve
→ confirmed Category→Zone Preference
→ local Policy resolve
→ local knowledge sufficient?
    yes → return local Policy
    no  → content privacy gate
          → bounded optional excerpt
          → AI semantic suggestion
          → local Policy validation
          → confirmed Preference for final Category
          → final local Policy resolve
```

它不会调用：

- Shell；
- M2 physical planner；
- Coordinator；
- Recovery；
- Writer；
- filesystem move / delete / archive。

输出仍然只是语义 `DesktopClassificationPolicyResult`，后续是否产生布局 Candidate、是否执行，继续由 M2/M3 已冻结链路负责。

## Local-first 调用策略

日常模式不应该把已经学会的事情重复问 AI。

### 本地知识足够

当本地已经得到：

- Category；以及
- 当前模板存在 Zone 时已经得到 TargetZone；

则：

```text
ProviderAttempted = false
content extractor calls = 0
```

即使用户打开了内容分析，也不会为了“顺便看看”而读取正文。

### 本地知识不足

下列情况才允许请求 AI：

- 没有 Category；
- 当前模板有 Zone，但只有 Category、没有 TargetZone。

AI 仍然只能产生 semantic suggestion，不能得到新的 authority。

### 本地同层冲突

如果本地 Explicit User / Local Policy 已经形成同层冲突：

```text
HasConflict = true
→ stop before AI
→ RequiresUserConfirmation = true
```

更低 authority 的 AI 不能充当用户规则仲裁器。

## Provider 故障时的降级

以下状态全部是 Intelligence 能力故障，而不是桌面执行授权：

- `NotConfigured`；
- `Unavailable`；
- `Timeout`；
- `Cancelled`；
- `InvalidResponse`。

若调用 AI 前已经有本地 Category / Zone 的部分结果，Provider 失败后必须完整保留该本地 Policy。

```text
local decision exists
+ AI failure
→ FinalPolicy = local Policy
```

若本地完全没有分类知识：

```text
local decision = null
+ AI failure
→ final decision = null
```

禁止制造默认 Category、默认 Zone 或伪造成功 Suggestion。

## AI 成功后的 Preference 重接

Preference 不参与 Category 决策。

因此 AI 成功后先由 Policy 确定最终 Category，再按该 Category 查询一次 confirmed Preference：

```text
AI suggestion
→ category winner
→ GetPreference(activeTemplateId, finalCategory)
→ final Policy resolve
```

这样首次由 AI 识别出的 Category 仍能复用 DesktopAgent 自己保存的长期 Category→Zone 偏好。

## 内容分析验收

M4-05 的隐私 gate 在 M4-06 日常工作流里继续成立：

- `contentAnalysisEnabled=false`：extractor 0 次调用，Provider request 的 excerpt 为 null；
- 本地知识足够：无论开关是否为 true，extractor 仍为 0 次调用；
- 只有确实需要 AI 且开关开启时才可能读取支持类型正文；
- Provider 永远不接收 local path / StableId。

## Authority 验收

最终决策仍由 M4-03 Policy Resolver 统一处理。

重要端到端例子：

```text
Explicit User Rule: Category = Course
AI Suggestion:      Category = Games, Zone = Z1

Final Category = Course     (Explicit User wins)
Final Zone     = Z1         (AI may fill an otherwise missing field)
RequiresUserConfirmation = true
```

这保持了 M4 已冻结的“字段独立解析”语义，没有为 M4-06 修改 Policy authority。

## M0-M3 安全边界验收

M4-06 不复制 Writer / Recovery 的实现，也不为了 AI 修改 M0-M3 production semantics。

完整 CI 必须继续运行并通过既有安全回归，包括：

- fresh observation；
- stale facts fail closed；
- stale / changed display topology；
- Coordinator gate；
- safety snapshot；
- Recovery checkpoint；
- verification / rollback；
- cooldown / dedup；
- fuse；
- Writer fail closed；
- B07 已完成的真实 Windows acceptance 所冻结行为。

M4 workflow 所在 `DesktopAgent.Application` assembly 继续不依赖：

- `DesktopAgent.Persistence`；
- `DesktopAgent.WindowsInterop`；
- `DesktopAgent.Poc`。

因此 M4 Intelligence 无法通过依赖倒置之外的“捷径”直接抓取 Windows Shell Writer。

## M4-06 自动验收矩阵

新增端到端行为测试覆盖：

1. local Rule + confirmed Preference 足够 → AI 0 / extractor 0；
2. AI success + bounded content → final Category 接回 confirmed Preference；
3. Explicit User Category 覆盖 AI Category；
4. NotConfigured / Unavailable / Timeout / Cancelled / InvalidResponse → 保留 local Policy；
5. content off → AI 可运行但正文 extractor 0；
6. local conflict → AI / extractor 均不运行；
7. no local knowledge + AI invalid → final Decision = null；
8. Application assembly 保持不依赖 Persistence / WindowsInterop / Poc；
9. 全仓库 M0-M4 regression suite 继续通过。

## 是否需要新的真机桌面验收

M4-06 新增的是 UI-agnostic Application 编排和自动验收，不修改：

- Windows Shell observation；
- physical coordinate planner；
- Writer；
- Recovery；
- WPF tray lifecycle；
- M3 unattended chain。

因此 M4-06 本身不新增一轮真实 Windows 图标移动验收要求。

M3-B B07 已冻结的物理桌面行为继续作为 v1.0 execution baseline。

## v1.0 CORE FEATURE FREEZE 条件

只有以下条件同时满足，才可以正式标记冻结：

- M4-06 Debug build PASS；
- M4-06 Release build PASS；
- 全量 Release tests 0 failure / error / timeout / aborted；
- 新增 M4-06 acceptance 全部通过；
- M0-M3 safety regression 全部继续通过；
- stacked PR 保持 Draft，不通过合并父 PR绕过验证；
- 未打开 production unattended gate；
- 未新增 M4 scope freeze 明确排除的能力。

通过后状态定义为：

```text
M0-M3 execution safety baseline: FROZEN
M4 intelligence core:            FROZEN
v1.0 CORE FEATURE FREEZE:        ACTIVE
```

下一阶段进入 M5 Productization。

## Freeze 后的变更规则

进入 M5 后，默认允许：

- Product UI / Developer Workbench 整理；
- WinUI 3 feasibility gate；
- installer / packaging / update；
- compatibility；
- performance profiling 与低风险优化；
- accessibility / usability；
- diagnostics；
- bug fix；
- release acceptance。

默认不再追加新的核心智能能力。

若必须改变 M0-M4 已冻结语义，应明确作为 freeze exception 记录原因、风险与回归范围，不能借“UI 调整”或“性能优化”暗改核心行为。

## v1.0 继续明确排除

- filesystem archive / delete / broad file manager；
- AI WriterCommand / physical coordinate；
- silent learned rules；
- Regex / arbitrary scripts；
- vector DB / embeddings / RAG；
- provider-owned Memory 作为核心依赖；
- Python product runtime；
- bundled local model；
- OCR / image / multimodal content understanding；
- PDF / Office full-content extractor；
- Multi-Agent；
- production unattended gate 自动开放。
