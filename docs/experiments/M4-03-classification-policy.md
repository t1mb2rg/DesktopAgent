# M4-03 Classification + Policy Engine

## 目标

M4-03 把 M4-00～02 已经得到的 AI Suggestion、用户明确规则和本地确定性规则放进同一个可解释 Policy，同时不复制 M2 已有的布局规则职责。

本阶段回答：

> 当多个来源都对“这是什么、应该去哪个 Zone”有意见时，DesktopAgent 如何稳定、可解释地决定最终语义结果？

## 与 M2 Rule 的关系

M2 已有 `DesktopLayoutIconRule` 是很窄的模板级布局例外：

- StableId 精确匹配；
- `Exclude`；
- `Pin`；
- `ZoneOverride`。

它不是通用分类引擎，也不应被扩展成 M4 的语义规则语言。

M4 新增 `DesktopClassificationRule`，负责：

- StableId 精确语义规则；
- 文件扩展名；
- 文件名有限 glob；
- ItemKind；
- 上述条件的有限 AND 组合；
- Category；
- 可选 TargetZone；
- authority / provenance / priority。

两者只在一个明确边界桥接：

> 若当前图标已有 M2 StableId `ZoneOverride`，它是用户对具体图标位置归属的明确决定，因此覆盖 M4 对 TargetZone 的建议。

M2 `Exclude` / `Pin` 不属于分类语义，仍完全留在后续布局执行层。

## Category 与 Zone 分开解析

M4-03 不把 Category 永久等同于一个 Zone。

Policy 分别选择：

```text
Category winner
TargetZone winner
```

最后再组合为 `DesktopItemClassificationDecision`。

因此：

- 高权威规则只声明 Category 时，不会把较低层但有效的 Zone 建议一起抹掉；
- M2 ZoneOverride 只覆盖 Zone，不伪造 Category；
- 未来同一 `Development` Category 可以随布局模板映射到不同 Zone。

## 决策优先级

Category：

```text
ExplicitUser
    > DeterministicLocal
    > AI
```

TargetZone：

```text
M2 StableId ZoneOverride
    > ExplicitUser
    > DeterministicLocal
    > AI
```

同一 authority 内：

```text
Priority DESC
Specificity DESC
stable CandidateKey ASC
```

Specificity 只在同 authority + priority 内做 tie-break，不允许一个“更具体”的低权威规则越过用户明确意图。

当前 bounded specificity：

- StableId exact = 1000；
- FileNamePattern = 100；
- FileExtension = 10；
- ItemKind = 1。

## 冲突语义

如果同一 authority / priority / specificity 层级中出现互相矛盾的 Category 或 TargetZone：

1. Resolver 仍使用稳定 CandidateKey 得到可重复结果；
2. 输出 `DesktopClassificationPolicyConflict`；
3. `RequiresUserConfirmation = true`；
4. UI 后续可以把冲突解释给用户，而不是让执行结果随集合顺序漂移。

低优先级被高优先级覆盖属于正常 shadowing，不算 hard conflict。

## Authority 与 Provenance 分离

`DesktopClassificationRuleAuthority`：

- `ExplicitUser`；
- `DeterministicLocal`。

`DesktopClassificationRuleProvenance`：

- `UserAuthored`；
- `BuiltIn`；
- `AiProposedAndConfirmed`；
- `LearnedAndConfirmed`。

这是刻意分开的。

例如：

```text
AI proposed
    ↓
user explicitly confirmed
    ↓
authority = ExplicitUser
provenance = AiProposedAndConfirmed
```

所以 AI 不会因为“最初是模型写的”而永久低于用户后来的明确确认；同时审计信息也不会丢失。

M4-04 的行为学习也会使用同一原则：证据或 AI 推断本身没有用户 authority，只有用户确认后才能升级成长期显式规则。

## 有界匹配语言

v1.0 不接受任意脚本或无界正则。

`DesktopClassificationRuleCondition` 只支持：

- exact StableId；
- extension；
- filename glob `*` / `?`；
- ItemKind；
- AND compound。

Filename glob 使用本地确定性 matcher，不调用 `Regex`，pattern 最大 256 字符。

单次 Policy Resolution 最多接受 4096 条规则，并只枚举到 `Max + 1` 后立即拒绝超限输入。

## AI 的位置

AI Suggestion 是最低 authority 的即时证据。

AI-only 决策默认 `RequiresUserConfirmation = true`。

AI 不会因为 confidence 很高就跨越本地确定性规则或用户显式规则。Confidence 描述模型自身置信度，不是执行权限分数。

## 故障与无决定

- 启用且实际参与当前决策的规则若引用不存在的 Zone，应 fail closed；
- AI Suggestion 若引用不存在的 Zone，应 fail closed；
- 没有任何 Category 候选时，Policy 可以返回 `Decision = null`；
- “没有分类结果”不是 Writer 授权，也不会自动生成物理坐标。

## 测试策略

M4-03 新增少量按行为分组的测试：

1. 有界 compound matcher + safe glob；
2. authority 优先于 numeric priority，用户确认 AI Rule 可升级为 ExplicitUser；
3. Category / Zone 独立解析，M2 ZoneOverride 只覆盖 Zone；
4. 同层冲突稳定 tie-break + conflict audit；
5. AI-only 仍要求确认，悬空 Zone fail closed。

不复制 M2 / M3 / Recovery 的既有安全回归测试。

## 本阶段不做

- 不持久化规则，SQLite 在 M4-04；
- 不做自然语言 `RuleDraft`，在 M4-05；
- 不做真实 Zone 几何生成；
- 不接 WPF / WinUI；
- 不把 ClassificationDecision 直接送入 Writer；
- 不改变 M2 Rule Resolver；
- 不改变 M3 / Recovery / Coordinator；
- 不打开 production unattended gate。
