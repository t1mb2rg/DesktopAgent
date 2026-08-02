# M2-03C-A：桌面图标用户规则核心模型

## 阶段目标

实现纯 Core 的桌面图标用户规则领域模型，包含：
- 规则定义（Exclude / Pin / ZoneOverride）
- 模板级 RuleSet
- StableId 精确匹配
- 目标显示器内规则分类
- OutOfScope 规则匹配
- Unmatched rules
- 严格引用身份不变量
- 不改变现有 PreviewPlanner 行为

## M2-03C 总体拆分

| 阶段 | 内容 |
|---|---|
| M2-03C-A | 规则领域模型 + 稳定匹配 + 确定性分类（本 PR） |
| M2-03C-B | Pin/Exclude 固定障碍、槽位过滤、ZoneOverride 优先分配、规则感知 Placement |
| M2-03C-C | SQLite Schema v3、规则 Store、WPF 规则编辑器、预览窗口规则展示 |

## 规则类型

### Exclude
- 图标不参与模板自动布局
- 未来执行时保持当前位置
- M2-03C-B 中会作为固定障碍占用空间

### Pin
- 图标由模板管理，但锁定当前像素位置
- 不产生移动目标
- M2-03C-B 中会作为固定障碍占用空间

### ZoneOverride
- 图标仍参与自动布局
- 强制分配到指定 Zone
- Zone 内具体槽位仍由确定性规划器决定
- 不表示任意像素坐标覆盖

## StableId 精确匹配

- 使用 `StringComparer.Ordinal`
- 区分大小写
- 不 Trim
- 不做路径规范化
- 不使用 DisplayName
- 不使用 ParsingName
- 不使用 Ordinal 匹配
- 只使用 StableId

## 规则唯一性

- 同一 RuleSet 中，同一 StableId 最多只能有一条规则
- 不存在 Exclude + Pin 等组合
- 重复 StableId 必须在 RuleSet 构造时拒绝（`StringComparer.Ordinal`）

## RuleSet

- 以模板为作用域（`TemplateId` 关联）
- 规则按 `StableId` `StringComparer.Ordinal` 升序保存
- 输入顺序不影响语义
- 防御性复制，只读公开

## ZoneOverride 悬空引用

- ZoneOverride 的 `TargetZoneId` 必须在 template.Zones 中存在（Guid 精确匹配）
- 即使没有图标匹配该规则，只要指向不存在 Zone 就必须 fail-closed
- 不得静默降级、按名称匹配、选择第一个 Zone 或忽略

## 目标显示器分类

- 使用 `string.Equals(icon.MonitorDeviceName, targetDisplay.DeviceName, StringComparison.Ordinal)`
- null `MonitorDeviceName` → OutOfScope
- 分区必须对 InputIcons 构成引用精确、无重叠、无遗漏的划分

## 规则匹配和应用

- 每个图标最多匹配一条规则
- Target 图标：Exclude → ExcludedMatches、Pin → PinnedMatches、ZoneOverride → ZoneOverrideMatches、无规则 → DefaultManagedIcons
- OutOfScope 图标：有规则 → OutOfScopeRuleMatches（`IsOnTargetDisplay = false`）
- 未匹配到图标的规则 → UnmatchedRules

## 引用身份不变量

- 图标主分区以引用身份验证，不比较 Ordinal/StableId
- 规则分区使用 `ReferenceEqualityComparer` 验证无重复、无遗漏
- Override 的 TargetZone 必须是 template.Zones 中的原对象
- Match 的 Icon 必须是 InputIcons 中的原对象
- Match 的 Rule 必须是 RuleSet.Rules 中的原对象
- 外部等值替代实例不能通过不变量

## 确定性顺序

- 图标集合按 Ordinal ASC
- 有效 Match 集合按 Icon.Ordinal ASC
- OutOfScopeRuleMatches 按 Icon.Ordinal ASC
- UnmatchedRules 按 StableId Ordinal ASC
- ZoneOverrideMatches 不按 Zone 分组重排
- 输入图标顺序、规则顺序、Zone 顺序不影响输出

## 本阶段不改变

- `DesktopLayoutPreviewPlanner` 的现有行为完全不变
- 不实现槽位规划、固定障碍、规则感知 Placement
- 不修改 Persistence、WPF/Poc、WindowsInterop、SQLite schema

## M2-03C-B 冻结语义（记录但不实现）

1. Pin 和 Exclude 都保持当前坐标
2. 两者都使用 `DesktopIconLayoutGeometry.CreateRectChecked(icon.PixelPosition, iconSpacing)` 生成固定障碍矩形
3. 所有与固定障碍发生半开矩形重叠的网格槽位都不可用
4. ZoneOverride 图标在指定 Zone 中优先于默认图标分配
5. Override 图标按 Ordinal ASC
6. 默认图标按 Ordinal ASC
7. Zone 按 SortOrder ASC
8. ZoneOverride 容量不足时，多余 Override 图标进入规则型 Unassigned
9. 默认图标只能使用 Override 分配后剩余的有效槽位
10. 旧的无规则规划路径语义保持不变

## M2-03C-C 边界（记录但不实现）

- SQLite Schema v3
- 规则 Store（CRUD）
- WPF 规则编辑器
- 预览窗口规则展示与调用

## 安全声明

- 不调用 Writer
- 不移动真实桌面图标
- 不保存安全快照
- 不创建恢复会话
