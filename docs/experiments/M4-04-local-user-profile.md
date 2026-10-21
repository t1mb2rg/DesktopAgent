# M4-04 Local User Profile + Learning Evidence

## 目标

M4-04 在 M4-03 Classification + Policy Engine 之上建立 DesktopAgent 自己拥有的长期用户 Profile。

本阶段回答：

> 用户明确规则、已确认偏好和日常行为证据应该如何长期保存，并在不把“推断”偷偷升级成“权限”的前提下参与后续分类？

长期记忆由 DesktopAgent 自己管理：

```text
AI Provider
    ↕
DesktopAgent User Profile
    ↓
SQLite
```

不依赖模型供应商 Memory，也不要求固定 AI Provider。

## 三类长期数据

M4-04 明确区分：

1. `DesktopClassificationRule`
2. `DesktopCategoryZonePreference`
3. `DesktopClassificationBehaviorEvidence`

它们不是三个名字不同的同一种对象。

### Rule

Rule 是正式 Policy 输入。

它拥有明确 authority 和 provenance，可以来自：

- 用户直接创建；
- DesktopAgent 内建确定性规则；
- AI 提议后用户确认；
- 行为学习草案经用户确认。

### Confirmed Preference

Preference 表示用户已经明确确认过的：

```text
Template + Category → PreferredZone
```

它是软偏好，不等于永久规则。

在 TargetZone 决策中的位置为：

```text
M2 StableId ZoneOverride
    > ExplicitUser Rule
    > Confirmed User Profile
    > DeterministicLocal Rule
    > AI Suggestion
```

Preference 只在当前 Template、Category 和有效 Zone 都匹配时参与决策。

### Behavior Evidence

Evidence 只记录一次“系统如何建议、用户最终如何反馈”的历史事实：

- Accepted
- Corrected
- Rejected

Evidence 本身没有 Policy authority。

它未来只能用于生成：

```text
PreferenceDraft / RuleDraft
    ↓
用户确认
    ↓
正式 Preference / Rule
```

因此不会出现“用户连续移动了几次图标，程序就静默学成永久规则”的行为。

## Authority 与 Provenance 继续分离

M4-03 的原则在持久化层继续成立。

例如：

```text
AI proposed
    ↓
user confirmed
    ↓
authority = ExplicitUser
provenance = AiProposedAndConfirmed
```

SQLite 保存两者，而不是只保存一个模糊的“来源”。

## Category 与 Zone 生命周期

Category 是跨模板的语义。

ZoneId 不是。

因此 M4 Rule 的可选目标 Zone 必须同时携带：

```text
TargetTemplateId + TargetZoneId
```

两者要么同时存在，要么同时不存在。

这意味着：

- “这是 Course”可以长期跨模板成立；
- “在 Template A 中把 Course 放到 CourseZone”只在 Template A 中成立；
- 不会把某个模板的 Zone GUID 误解释成全局 Category 映射。

保存带 Zone 的 Rule 时，Store 会验证目标 Zone 确实属于目标 Template。

如果模板或 Zone 后续发生变化，当前有效布局仍由 Policy 的 fresh valid Zones 决定，持久化数据不能绕过当前事实。

## SQLite v6

共享数据库版本升级为 v6。

新增三张表：

### `desktop_classification_rules`

保存 M4 语义规则，包括：

- condition
- category
- optional template-bound target zone
- authority
- provenance
- priority
- enabled state

`SqliteDesktopClassificationRuleStore` 使用完整 upsert。

读取规则按稳定 RuleId 顺序返回。

单次最多读取 4096 条规则，SQL 只取 `Max + 1`：

```text
0..4096   → 正常读取
4097th    → fail closed
```

因此数据库异常膨胀不会直接转化为无界 RAM 工作集。

### `desktop_user_category_zone_preferences`

保存用户已确认的 Category → Zone 偏好。

唯一语义键为：

```text
TemplateId + Category (NOCASE)
```

同一个模板内 `Development` 与 `development` 被视为同一偏好。

保存时验证 PreferredZone 属于对应 Template。

### `desktop_classification_evidence`

保存不可变行为证据。

Evidence 采用 append-only：

- 新 EvidenceId 可以追加；
- 重复 EvidenceId 显式拒绝；
- 不做静默覆盖。

最近证据读取：

```sql
ORDER BY observed_at_utc DESC, evidence_id ASC
LIMIT @limit
```

单次读取上限为 512 条。

长期历史留在 SQLite，调用方只拿小窗口，不把整个学习历史常驻 RAM。

## 删除与隐私边界

`ClearEvidenceAsync` 只清空行为历史。

它不会同时删除：

- 用户明确 Rule；
- 已确认 Preference。

因此“清学习历史”和“重置用户规则”是两个不同操作，后续 UI 可以分别提供入口。

## 数据损坏策略

Persistence 不尝试猜测或静默修复已经违反领域契约的数据。

读取时会重新验证：

- Guid
- enum
- bool
- UTC timestamp
- Rule condition
- authority / provenance
- feedback semantics
- template/zone pair contract

若数据库内容不能恢复成合法领域对象，直接 `InvalidDataException` fail closed。

这和 M0 到 M3 的安全风格保持一致：Store 记录证据，但不能通过“容错猜测”改变上层语义。

## Schema migration

共享 `SqliteDesktopAgentSchema` 统一管理 `PRAGMA user_version`。

所有 SQLite Store 初始化都会到达完整 v6，而不是各 Store 自己维护版本号。

迁移测试覆盖：

- fresh v0 → v6；
- v1 snapshot 数据保留；
- v2 template / zone 数据保留；
- v3 M2 icon rule 数据保留；
- v4 recovery evidence 经 v5 迁移语义保持；
- v5 → v6 创建 M4 表且旧数据保留；
- future version 拒绝；
- foreign key / CHECK constraint；
- repeated initialization 幂等。

## 测试整理

M4-04 没有以“测试数字减少”为目标。

本阶段合并的是旧 Schema 测试里重复的版本号和初始化顺序断言，同时保留独立行为：

- migration data preservation
- Recovery evidence
- foreign keys
- corruption fail closed
- transaction behavior
- cancellation

M0 到 M3 Writer / Recovery / rollback / stale observation / topology / Coordinator / fuse / cooldown / B07 语义均不修改。

## 性能与内存边界

M4-04 的轻量策略：

- Rule 读取有 4096 硬上限；
- Evidence 使用 SQL `LIMIT`，最多 512；
- 不加载完整历史；
- 不引入 vector DB；
- 不引入 embeddings cache；
- 不引入 Python runtime；
- 不引入 provider-owned Memory。

## 本阶段不做

- 不自动把 Evidence 升级成 Rule；
- 不生成自然语言 `RuleDraft`，留给 M4-05；
- 不做 embedding / vector DB；
- 不做 RAG；
- 不做 OCR / 图片理解；
- 不让 AI 看到或决定最终像素坐标；
- 不接 WriterCommand；
- 不修改 M0 到 M3 Recovery / Writer 安全语义；
- 不打开 production unattended gate。
