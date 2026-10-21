# M5-01 Automation Policy

## 目标

M5-01 建立独立于 UI、Writer 和 Pending Queue persistence 的自动化策略边界。

本阶段只回答：

> 对于已经形成的候选桌面动作，当前允许 Auto / Queue / Ask / Block 中的哪一种处理？

M5-01 不负责再次分类，也不直接执行任何物理桌面操作。

## Automation Mode

- `Manual`
- `Assisted`（默认）
- `Automatic`

Automation Mode 表示用户允许系统达到的最大后台自动化级别，不改变 AI 的 authority。

`Automatic` 不等于 AI 获得 Writer authority。

## Disposition

- `Auto`：当前动作已有足够授权，可以继续，不需要再次询问。
- `Queue`：当前不能自动执行，但问题可以延期处理。
- `Ask`：当前流程必须立即获得用户决定才能继续。
- `Block`：当前状态禁止执行，普通确认不能绕过。

`Queue` 与 `Ask` 的区别不是问题“严重程度”，而是问题能否延期处理。

## Authorization Basis

M5-01 不直接依赖某一种 Classification 实现，而只接收影响本次物理动作的授权基础：

- `DirectUserRequest`：用户当前明确发起本次动作。
- `ConfirmedUserPolicy`：用户此前已明确授权某类未来行为。
- `DeterministicLocal`：可信、本地、确定性策略形成候选动作，但没有用户长期授权。
- `AiOnly`：当前候选动作的关键授权只来自 AI 新判断。
- `None`：不存在执行授权来源。

Authorization Basis 是执行权限语义，不等于分类结果的 Source 字段。

## 核心矩阵

| Authorization Basis | Manual | Assisted | Automatic |
|---|---|---|---|
| `DirectUserRequest` | Auto | Auto | Auto |
| `ConfirmedUserPolicy` | Queue | Auto | Auto |
| `DeterministicLocal` | Queue | Queue | Auto |
| `AiOnly` | Queue | Queue | Queue |
| `None` | Queue | Queue | Queue |

### Manual

只有用户当前明确发起的动作可以直接继续。

DesktopAgent 仍然可以理解、规划和排队，但不会因为已有后台规则或本地确定性判断而自行修改桌面。

### Assisted

这是默认模式。

- 当前用户明确请求可直接继续；
- 用户过去明确确认过的长期策略可以自动继续；
- 新的本地确定性判断或 AI-only 判断进入 Queue。

### Automatic

在 Assisted 的基础上，允许可信本地确定性策略直接继续。

AI-only 新判断仍不能直接 Auto。

## Hard override

以下状态拥有最高优先级：

```text
SafetyBlocked
→ Block

LayoutEditing
→ Block
```

Hard Block 高于 Mode、Authorization Basis 和普通用户确认。

布局编辑期间禁止旧布局语义与 `LayoutDraft` 并发执行。

## Queue / Ask

需要用户解决的问题按是否可延期处理：

```text
RequiresUserResolution
    + CanDefer
    → Queue

RequiresUserResolution
    + !CanDefer
    → Ask
```

因此 AI-only 新判断默认进入 Queue，而不是逐项弹出确认窗口。

M4 Classification 的 `RequiresUserConfirmation` 不能机械映射成 `Ask`；M5 调用方需要根据当前产品流程判断是否可以延期。

## 无可执行决定

`HasExecutableDecision = false` 时永远不能进入 Auto。

- 若当前流程必须立即补齐决定，则 `Ask`；
- 否则 `Queue`。

用户当前发起操作也不能绕过“没有可执行决定”的事实。

## Direct User Request

用户当前明确发起的动作已经获得本次执行授权，因此：

```text
DirectUserRequest
→ Auto
```

在 Manual / Assisted / Automatic 三种模式下均成立。

这避免出现“用户点击整理后，系统再次询问是否整理”的重复确认。

但 Direct User Request 不能绕过：

- Safety Block；
- Layout Editing Block；
- 必须立即解决的语义冲突；
- 缺失可执行决定。

## 核心产品原则

> 用户确认的是“以后什么可以自动做”，不是每次移动都确认。

用户确认过的长期策略可在 Assisted / Automatic 中直接 Auto。

AI-only decision 在任何 Automation Mode 下都不能因为模型 confidence 或模式设置直接获得 Writer authority。

## 与 M4 的关系

M4 Classification Policy 继续负责：

- Category / TargetZone 语义决策；
- authority / provenance；
- conflict audit；
- `RequiresUserConfirmation`；
- AI-only suggestion 与 confirmed Rule / Preference 的优先级。

M5-01 不复制上述逻辑。

调用方未来负责把已有 Classification / Layout / User action 事实适配成 `DesktopAutomationPolicyContext`。

## 本阶段不做

- Pending Queue persistence；
- Confirm & Remember；
- WinUI Settings / Automation Mode UI；
- Writer integration；
- production unattended gate；
- Layout Editor；
- 修改 Recovery / Coordinator safety semantics；
- AI 生成 WriterCommand 或 physical coordinates。

## M5-01 Gate

必须满足：

- `SafetyBlocked` / `LayoutEditing` 永远 `Block`；
- `DirectUserRequest` 在三种 Mode 下均可 `Auto`，但不能绕过 Hard Block / unresolved execution；
- `ConfirmedUserPolicy` 只有 Assisted / Automatic 可后台 `Auto`；
- `DeterministicLocal` 只有 Automatic 可后台 `Auto`；
- `AiOnly` 在所有 Mode 下都不能 `Auto`；
- `Queue` / `Ask` 由问题是否可延期决定；
- 无 executable decision 永远不能 `Auto`；
- Policy 保持 UI-agnostic / IO-agnostic，不接 Writer。
