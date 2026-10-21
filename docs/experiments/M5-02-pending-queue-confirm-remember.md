# M5-02 Pending Queue + Confirm & Remember

## 目标

M5-02 把 M5-01 的 `Queue` disposition 变成可持久化、可稍后处理的产品语义，并建立 Confirm & Remember 的长期授权边界。

本阶段仍不执行 Writer，不移动真实桌面图标。

## Pending Queue

只有 `DesktopAutomationDisposition.Queue` 可以进入 Pending Queue。

Pending Action 保存：

- PendingActionId / CreatedAtUtc
- DesktopItemFacts
- 可选 DesktopItemClassificationDecision
- 当前 TemplateId（若 Zone 语义需要）
- Authorization Basis
- Queue Reason

不保存：

- 物理坐标
- WriterCommand
- COM / PIDL / Shell 对象
- 旧的执行计划

`NoExecutableDecision` 可以进入 Queue，但必须保持 `Decision = null`；它不能被直接 Confirm，只能由后续产品流程补全决定或 Reject。

## Queue / Ask 边界

M5-02 只持久化 `Queue`。

`Ask` 仍表示当前交互必须立即解决，因此不会被偷偷降级成后台 Pending Action。

## Confirm Once

Confirm Once：

- 从 Pending Queue 移除该动作；
- 只为这一次后续 fresh execution 返回 `DirectUserRequest` authority；
- 不创建长期 Rule / Preference；
- 不直接调用 Writer。

## Confirm & Remember

长期记忆范围必须由调用方显式选择，DesktopAgent 不会从一次点击中猜测更宽泛的授权范围。

### ExactItem

创建 `ExplicitUser` Classification Rule：

- Condition = StableId
- RuleId = PendingActionId
- AI proposal 保留 `AiProposedAndConfirmed` provenance

### FileExtension

创建 `ExplicitUser` Classification Rule：

- Condition = FileExtension
- 只有 Pending Action 本身存在扩展名时才允许
- RuleId = PendingActionId

这是一个明确的泛化动作，UI 后续必须让用户看见自己正在授权“以后同扩展名也这样处理”。

### CategoryZonePreference

创建 / 更新已确认的 `Category → Zone` User Profile Preference：

- 必须同时存在 TemplateId 与 TargetZoneId
- PreferenceId = PendingActionId
- 用户确认后 Confidence = 1.0
- 已有同 Template + Category 偏好时累加 EvidenceCount

Preference 是已确认的软偏好，不会把 AI-only classification 本身自动升级成 Rule authority。

## 崩溃 / 重试边界

Confirm & Remember 先写长期记忆，再删除 Pending Action。

长期 Rule / Preference 使用 `PendingActionId` 作为稳定 ID，因此若进程在“记忆写入成功、Queue 删除前”崩溃，重新处理同一 Pending Action 不会制造一组新的重复规则。

这不是跨 Store 原子事务；M5-02 不假装 SQLite Store 与未来 Writer 组成一个事务。

## SQLite V7

共享 Schema 升级到 `user_version = 7`，新增：

- `desktop_pending_actions`
- `idx_pending_actions_created_at_utc`

Store 读取始终有界，当前最大 Pending 工作集为 512。

## M5-02 不做

- WinUI Pending Queue 页面
- Writer integration
- 自动执行 Confirm 结果
- production unattended gate
- Layout Editor
- Recovery / Coordinator safety semantic 修改
- 从一次确认中隐式猜测 Remember Scope
- 把 AI-only confidence 当作 Writer authority

## Gate

M5-02 Gate 需要：

- Core contract tests PASS
- Pending SQLite round-trip / bounded ordering PASS
- shared schema V7 migration PASS
- Confirm Once / Confirm & Remember contract tests PASS
- full Debug / Release build PASS
- full Release tests PASS
- 相对 M5-01 scope audit 无 WinUI / Writer / Recovery 越界
