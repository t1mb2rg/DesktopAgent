# M2-03C-B：规则感知的布局规划

## 阶段目标

实现纯 Core 的规则感知布局规划器，消费 M2-03C-A 的规则解析结果，使 Exclude/Pin/ZoneOverride 规则真正参与分配和 Placement，生成包含固定障碍、Override 优先分配和默认图标剩余槽位分配的完整布局规划。

## 输入和输出模型

### 输入

| 参数 | 类型 | 来源 |
|---|---|---|
| `resolvedTemplate` | `ResolvedDesktopLayoutTemplate` | M2-02A 模板解析 |
| `resolutionPlan` | `DesktopLayoutIconRuleResolutionPlan` | M2-03C-A 规则解析 |
| `iconSpacing` | `PixelPoint` | 模板解析时确定 |

### 输出

`DesktopLayoutRuleAwarePlacementPlan` — 不可变 sealed class，包含：

- **FixedItems**: `ExcludedItems` + `PinnedItems`，包含 Icon、Rule、FixedRect
- **Override 分配**: `OverrideAssignedItems` + `OverrideUnassignedItems`
- **Default 分配**: `DefaultAssignedItems` + `DefaultUnassignedItems`
- **组合视图**: `AllFixedItems`、`AllAssignedItems`、`FixedObstacleRects`
- **统计**: 各分区计数、IsComplete、HasFixedItems 等布尔标志

### 新增模型

| 类 | 职责 |
|---|---|
| `DesktopLayoutRuleAwareAssignmentSource` | 枚举：Override / Default |
| `DesktopLayoutRuleAwareFixedItem` | 固定障碍项（Exclude/Pin），含 Icon、Rule、FixedRect |
| `DesktopLayoutRuleAwareAssignedItem` | 已分配项，含 Icon、Zone、SlotIndex、坐标、来源、可选 Rule |
| `DesktopLayoutRuleAwarePlacementPlan` | 完整规划结果，含所有分区和不变量验证 |
| `DesktopLayoutRuleAwarePlacementPlanner` | 无状态纯函数规划器 |

## 固定障碍语义

1. Pin 和 Exclude 规则匹配的图标都保持当前像素坐标。
2. 固定障碍矩形使用 `DesktopIconLayoutGeometry.CreateRectChecked(icon.PixelPosition, iconSpacing)` 生成半开矩形 `[pos, pos+spacing)`。
3. 所有与任一固定障碍发生半开矩形重叠的候选槽位均不可用。
4. 矩形边界仅接触但无面积重叠时，槽位仍可用。
5. 固定障碍可以位于 Zone 内、Zone 外或跨 Zone。
6. 跨 Zone 障碍正确过滤多个 Zone 中重叠的槽位。

## 半开矩形重叠语义

遵循 `DesktopIconLayoutGeometry.Overlaps`:
```
a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top
```
相邻边界（`a.Right == b.Left` 或 `a.Bottom == b.Top`）不视为重叠。

## ZoneOverride 优先级

1. Override 图标按 `Icon.Ordinal ASC` 分配。
2. 每个 Override 只能进入其 `TargetZoneId` 指定的 Zone。
3. 在该 Zone 内，Override 按 Ordinal 顺序优先占用可用槽位。
4. 指定 Zone 的可用槽位不足时，多余 Override 图标进入 `OverrideUnassignedItems`。
5. Override 不得借用其他 Zone 的空闲槽位。
6. 不同 Zone 的 Override 输出不按 Zone 分组重排 — 保持全局 Ordinal ASC 顺序。

## 默认分配

1. 默认图标按 `Icon.Ordinal ASC` 分配。
2. Zone 按 `SortOrder ASC` 遍历。
3. 默认图标只能使用 Override 分配后剩余的有效槽位。
4. 容量不足时进入 `DefaultUnassignedItems`。
5. 空规则集路径与旧无规则规划语义一致。

## 槽位枚举和过滤

1. 对每个 Zone 枚举全部 `TheoreticalCapacity` 个槽位。
2. 按 `PlacementFlow`（ColumnMajor 或 RowMajor）确定的 `SlotIndex` 顺序遍历。
3. 每个槽位计算 `TargetCellBounds`，检查与所有固定障碍矩形的重叠。
4. 未被阻挡的槽位进入 `availableSlots` 列表。
5. 分配时从 `availableSlots` 按序消费，同时受 `EffectiveCapacity` 上限约束。

## 确定性保证

- 图标集合按 `Ordinal ASC` 排序后处理
- Override 按 `Icon.Ordinal ASC`
- Default 按 `Icon.Ordinal ASC`
- Zone 按 `SortOrder ASC`
- 输入图标顺序、规则顺序、Zone 顺序不影响输出
- 同一输入重复调用产生相同结果

## 引用身份不变量

- **Icon**: 所有输出中的 Icon 引用必须是 InputIcons 中的原对象
- **Rule**: FixedItem.Rule 和 AssignedItem.Rule（Override）必须是 RuleSet 中的原对象
- **Zone**: AssignedItem.Zone 必须是 ResolvedTemplate 中的原对象
- **分区无重复无遗漏**: Excluded + Pinned + OverrideAssigned + OverrideUnassigned + DefaultAssigned + DefaultUnassigned 完整覆盖全部 TargetDisplayIcons
- **外部等值替代实例**被 Plan 构造器拒绝

## 与旧无规则路径的兼容关系

- 空 RuleSet 路径产生与旧 `DesktopLayoutAssignmentPlanner` + `DesktopLayoutCoordinatePlanner` 相同的结果
- 旧 Planner 公开 API 完全不变
- 新增 Planner 通过组合和独立路径实现，不破坏旧语义

## 测试和验证结果

### 新增测试：46 个

**A. 固定障碍 (8)**
- Exclude 生成固定障碍且不产生目标位置
- Pin 生成固定障碍且不产生目标位置
- 单个固定障碍过滤一个槽位
- 单个固定障碍过滤多个槽位
- 多个固定障碍共同过滤槽位
- 边界接触但无面积重叠时不过滤
- 跨 Zone 障碍正确过滤多个 Zone
- Zone 外障碍不错误过滤无重叠槽位

**B. ZoneOverride (8)**
- Override 图标只进入指定 Zone
- Override 比默认图标优先获得槽位
- 多个 Override 按 Ordinal ASC
- Override 规则输入乱序不改变结果
- 不同 Zone 的 Override 不按 Zone 错误重排
- 指定 Zone 无有效槽位时进入 Unassigned
- 部分容量不足，前序合法 Override 保留，其余 Unassigned
- Override 不得借用其他 Zone 空闲槽位

**C. 默认分配 (6)**
- 默认图标使用 Override 后剩余槽位
- 默认图标按 Ordinal ASC
- 容量不足进入 DefaultUnassigned
- 紧容量 Zone 正常处理
- 空默认图标输入正常处理
- 空规则集路径与旧规划一致

**D. 不变量和安全 (9)**
- Planner 拒绝 null ResolvedTemplate
- Planner 拒绝零/负 iconSpacing
- 外部等值 Icon 被 Plan 构造器拒绝
- 分区无重复、无遗漏
- TargetPosition 唯一
- TargetCellBounds 与 Zone 和 SlotIndex 一致
- 被固定障碍过滤的槽位绝不出现在 Assigned 中
- OutOfScope 和 UnmatchedRules 不进入布局结果
- 输入顺序不影响输出

**模型不变量 (9)**

**边界场景 (6)**

### 全量测试：1045/1045 通过，0 失败，0 跳过

## 安全边界

- 不调用 Writer
- 不移动真实桌面图标
- 不保存安全快照
- 不创建恢复会话
- 不修改 SQLite schema
- 不修改 Persistence、Poc、WindowsInterop
- 纯 Core 实现，无副作用

## M2-03C-C 的明确后续范围

- SQLite Schema v3（规则表）
- 规则 Store（CRUD）
- WPF 规则编辑器
- WPF Preview 窗口规则展示和调用
- 规则持久化
