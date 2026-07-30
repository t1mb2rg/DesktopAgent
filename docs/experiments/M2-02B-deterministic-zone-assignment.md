# M2-02B：图标到分区的确定性分配

## 阶段目标

在 `ResolvedDesktopLayoutTemplate`（已解析为像素坐标的分区）之上，
将 `DesktopIconSnapshotItem` 图标集合确定性地分配到合适的分区。

纯 Core 计算，不依赖 Shell COM、WPF、SQLite 或显示器 API。

## 为什么使用 DesktopIconSnapshotItem

- `DesktopIconSnapshotItem` 是快照中的完整图标记录（Ordinal、StableId、DisplayName 等）
- M2-02B 的图标输入来源于快照恢复流程，而非当前的实时读取
- `DesktopIconDescriptor` 来自实时读取，可能缺少快照中存在的图标（缺失/新增）

## Ordinal 是确定性图标顺序

所有图标排序使用且仅使用 `Ordinal ASC`：

- `Ordinal` 为快照中图标的原始位置索引，非负且唯一
- 相同输入集合在不同调用顺序下产生相同结果
- 不依赖调用方传入的 List 顺序
- `Ordinal` 允许不连续（例如只有 0、5、100）

## 为什么不依赖 List 输入顺序

调用方可能以任意顺序传入图标集合。
Planner 内部始终将输入排序为 `Ordinal ASC`，
保证相同输入集合产生相同分配结果。

## StableId 使用 Ordinal 唯一性比较

- `StableId` 在输入集合中必须唯一（`StringComparer.Ordinal`）
- 大小写不同的 StableId（如 `abc` 和 `ABC`）视为不同，允许分配
- 前后空格不同的 StableId 视为不同

## Zone 使用 SortOrder ASC

- 图标按 `Zone.Zone.SortOrder ASC` 依次填充分区
- 不使用 `ResolvedTemplate.Zones` 的模板输入顺序
- SortOrder 唯一性由 `DesktopLayoutTemplate` 在构造时保证
- ZoneId、Zone Name、PixelBounds、PlacementFlow 不影响分配顺序

## 为什么不使用模板输入顺序

模板输入顺序是 Zones 列表的构造顺序，可与 SortOrder 不同。
SortOrder 是独立字段，用于后续规划器的确定性优先级。

## EffectiveCapacity 分配语义

```
foreach zone in zonesOrderedBySortOrder:
    remaining = zone.EffectiveCapacity
    while cursor < icons.Count && assigned.Count < remaining:
        assigned.Add(icons[cursor])
        cursor++
    yield ZoneAssignment(zone, assigned)
```

- 使用 `long` 的 `EffectiveCapacity` 安全比较
- 不将容量强转为 `int`
- 图标最多分配一次（单游标顺序分配）
- 不回填或轮询分区
- 不做负载均衡
- 不按图标类型分类

## 零容量 Zone

- 容量为 0 的 Zone 生成一个空的 `DesktopLayoutZoneAssignment`
- `IsEmpty = true`，`AssignedCount = 0`
- 后续 M2-02C 阶段跳过空分配
- 零容量分区仍在 Plan 中保留（方便诊断和验证）

## 容量不足返回 UnassignedIcons

图标数量超过总有效容量时：
- 已分配部分完整返回
- 剩余图标进入 `UnassignedIcons`（保持 Ordinal ASC）
- `IsComplete = false`
- **不在 Planner 中抛异常**

## 为什么容量不足不在 Planner 抛异常

- Planner 是纯数据分配器，不连接 Writer
- 调用方（Coordinator 或后续执行器）负责拒绝不完整 Plan
- 允许上层在 Writer 执行前添加额外的拦截逻辑

## PlacementFlow 留给 M2-02C

`PlacementFlow`（ColumnMajor / RowMajor）不在此阶段使用。
它的作用是将 Zone 中第 N 个图标映射为具体 `PixelPoint` 坐标。
M2-02B 只确定 Icon → Zone 和 Zone 内的稳定顺序。

## 防御性复制

- `DesktopLayoutZoneAssignment` 构造时对 `icons` 执行 `ToList()` + `AsReadOnly()`
- `DesktopLayoutAssignmentPlan` 构造时对三个集合均执行 `ToList()` + `AsReadOnly()`
- 修改原始输入集合不影响 Plan

## Plan 的完整分区不变量

`DesktopLayoutAssignmentPlan` 使用多层 fail-closed 构造验证：

1. **InputIcons**：Ordinal 唯一、StableId 唯一（Ordinal）、严格升序、无 null。
   建立 `Dictionary<int, DesktopIconSnapshotItem> inputByOrdinal` 用于后续完整身份匹配。

2. **ZoneAssignments**：数量等于 ResolvedTemplate.Zones.Count、无 null、
   ZoneId 唯一、每个 Zone 通过 `ReferenceEquals` 确认来自同一 ResolvedTemplate、
   按 `Zone.Zone.SortOrder ASC` 排序。

3. **图标身份必须按完整 (Ordinal, StableId) 组合匹配**：
   通过 `inputByOrdinal[icon.Ordinal]` 获取预期项，
   然后 `StringComparer.Ordinal.Equals(expected.StableId, icon.StableId)`。
   不分别使用 `inputOrdinals.Contains()` + `inputStableIds.Contains()`。

4. **整体确定性序列**：
   ZoneAssignments(按 SortOrder) + Unassigned 顺序拼接，
   逐项与 `inputList[i]` 比较 (Ordinal, StableId)。
   序列长度 = inputList.Count，不重不漏不额外。

5. **UnassignedIcons**：无 null、严格 Ordinal ASC、Ordinal 不重复、
   StableId 不重复（Ordinal）、每项与 InputIcons 完整 (Ordinal, StableId) 匹配、
   不与 Assigned 重复。

6. **计数不变量**：
   `checked` totalAssigned + unassignedList.Count == inputList.Count；
   totalAssigned ≤ int.MaxValue；
   totalAssigned ≤ TotalEffectiveCapacity。

## 本阶段边界

**实现**：
- `DesktopLayoutZoneAssignment`（单分区分配结果）
- `DesktopLayoutAssignmentPlan`（完整分配计划）
- `DesktopLayoutAssignmentPlanner`（静态 Plan 方法）
- 测试：Planner 25 + Models 26 = 51 tests
- 实验文档

**不实现**：
- 图标分类规则
- pin / exclude / override
- 具体 PixelPoint
- WPF 预览
- Writer 执行
- 真实机验证

## M2-02C 后续职责

- 输入：`DesktopLayoutAssignmentPlan`
- 按 `PlacementFlow` 生成具体 `PixelPoint` 坐标
- ColumnMajor：纵向优先
- RowMajor：横向优先

## M2-03C 用户规则如何在以后扩展分配输入

M2-02B 使用 `DesktopIconSnapshotItem` 作为分配输入。
后续 M2-03C 可以在 Planner 入口前引入过滤/规则层：
- 按 ItemKind 筛选（排除系统项目）
- 按 MonitorDeviceName 匹配
- 用户自定义排除/覆盖列表
- 经规则处理后的图标集合再传入 Planner
