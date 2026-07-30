# M2-02C：分区内具体目标坐标规划

## 阶段目标

在 M2-02B 的 `DesktopLayoutAssignmentPlan`（图标→分区）基础上，
为每个已分配图标生成具体像素坐标 (`PixelPoint`) 和占用矩形 (`PixelRect`)。
纯 Core 计算，不涉及 Shell COM、WPF、SQLite 或显示器 API。

## 输入是 DesktopLayoutAssignmentPlan

M2-02C 接收 M2-02B 的完整 Assignment Plan：

- `ResolvedTemplate`：已解析为像素坐标的模板（含 `IconSpacing`、`WorkArea`、`Zones`）
- `ZoneAssignments`：每个分区的图标分配结果（`DesktopLayoutZoneAssignment`）
- `UnassignedIcons`：未分配的图标

不直接接收 `ResolvedDesktopLayoutTemplate` 或原始图标集合。

## 为什么目标点是网格单元左上角

`TargetPosition` 是图标网格单元左上角，不是中心点。

原因：

- `PixelPoint` 在 Windows 桌面坐标系中，图标放置使用左上角锚点
- `DesktopIconLayoutGeometry.CreateRectChecked(TargetPosition, IconSpacing)` 直接生成 `[pos, pos+spacing)` 半开矩形
- 与现有 `DesktopIconLayoutGeometry` 的语义一致
- 后续 Writer 执行时使用左上角坐标作为 `POINT` 结构

不要：

- 居中图标
- 添加半个 spacing
- 使用浮点数

## 半开 TargetCellBounds

占用矩形使用现有：

```
DesktopIconLayoutGeometry.CreateRectChecked(TargetPosition, IconSpacing)
```

其半开区间语义为：

```
[TargetPosition.X, TargetPosition.X + IconSpacing.X)
×
[TargetPosition.Y, TargetPosition.Y + IconSpacing.Y)
```

`TargetCellBounds.Width == IconSpacing.X` 和 `TargetCellBounds.Height == IconSpacing.Y` 由 `CreateRectChecked` 保证。

## Zone 左上角局部锚点

锚点固定为：

```
zone.PixelBounds.Left
zone.PixelBounds.Top
```

不使用 `WorkArea` 全局原点、主显示器原点、原图标 `PixelPosition`。

非零 `Zone.Left` / `Zone.Top` 被正确作为锚点，包括负虚拟桌面坐标。

## ColumnMajor 公式

ColumnMajor 是纵向优先：一列填满后进入下一列。

```
columnIndex = slotIndex / zone.RowCount
rowIndex    = slotIndex % zone.RowCount
```

例：3列 × 2行

```
slot 0 → col0, row0
slot 1 → col0, row1
slot 2 → col1, row0
slot 3 → col1, row1
slot 4 → col2, row0
slot 5 → col2, row1
```

## RowMajor 公式

RowMajor 是横向优先：一行填满后进入下一行。

```
rowIndex    = slotIndex / zone.ColumnCount
columnIndex = slotIndex % zone.ColumnCount
```

例：3列 × 2行

```
slot 0 → col0, row0
slot 1 → col1, row0
slot 2 → col2, row0
slot 3 → col0, row1
slot 4 → col1, row1
slot 5 → col2, row1
```

执行除法前确保对应 `RowCount` / `ColumnCount` 大于 0。

## long 中间运算与 checked 溢出策略

坐标计算使用 `long` 中间运算，随后 `checked` 转换为 `int`：

```csharp
long targetX = (long)zone.PixelBounds.Left
             + (long)columnIndex * iconSpacing.X;

long targetY = (long)zone.PixelBounds.Top
             + (long)rowIndex * iconSpacing.Y;

TargetPosition = new PixelPoint(
    checked((int)targetX),
    checked((int)targetY));
```

不使用可能溢出的 `int` 乘加。`checked` 转换失败时 fail-closed：
当前实现将 `OverflowException` 包装为 `InvalidOperationException`，
原 `OverflowException` 保存在 `InnerException` 中。

## 只枚举实际 Assigned 图标

Planner 只遍历 `assignment.Icons.Count` 个实际分配的图标，每个从 `SlotIndex = 0` 到 `Count - 1`。

不对 `EffectiveCapacity` 或 `TheoreticalCapacity` 进行循环。
不预生成空槽位或全部理论容量。

这对大容量 Zone（`EffectiveCapacity > int.MaxValue`）很重要：
即使容量为 2,500,000,000，也只按实际分配的少量图标（如 100 个）循环。

## MaxItemCount / EffectiveCapacity 边界

- `slotIndex < 0` → 拒绝
- `(long)slotIndex >= zone.EffectiveCapacity` → 拒绝
- `(long)slotIndex >= zone.TheoreticalCapacity` → 拒绝（额外检查）
- `zone.EffectiveCapacity == 0` → 拒绝任何 Placement

`MaxItemCount` 限制通过 `EffectiveCapacity = Min(TheoreticalCapacity, MaxItemCount)` 间接生效。

## 零容量 Zone

容量为 0 的 Zone：

- 不允许创建任何 `DesktopIconTargetPlacement`（slotIndex 0 被拒绝）
- 对应 `DesktopLayoutZonePlacement` 的 `IsEmpty = true`、`PlacementCount = 0`
- Planner 仍为其创建空的 `ZonePlacement`（保持结构完整）

## 不完整计划的部分坐标语义

当 `assignmentPlan.IsComplete == false` 时：

- 已分配图标仍生成合法坐标
- `UnassignedIcons` 原样保留（顺序和身份不变）
- `PlacementCount < InputCount`
- `IsComplete == false`
- `PlacementCount + UnassignedCount == AssignmentPlan.InputCount`

不在 M2-02C 因容量不足抛异常。Writer 接入阶段必须拒绝不完整计划。

## UnassignedIcons 不生成目标

`UnassignedIcons` 从 `AssignmentPlan` 防御性复制到 `PlacementPlan`。
不为它们创建 `DesktopIconTargetPlacement`。

## 图标原位置不影响目标

`DesktopIconSnapshotItem.PixelPosition` 是图标当前的 Explorer 位置。
M2-02C 完全忽略它 — 目标坐标仅由 Zone 锚点、网格、SlotIndex 推导。

同样忽略：`DisplayName`、`ItemKind`、`MonitorDeviceName`、`ParsingName` 等元数据。

## 当前不处理移动碰撞与循环

M2-02C 实现 `PlacementPlan` 内部的重复位置和单元重叠不变量验证：

- `DesktopLayoutZonePlacement` 验证 Zone 内 `TargetPosition` 不重复、`TargetCellBounds` 不重叠
- `DesktopLayoutPlacementPlan` 验证全局 `TargetPosition` 不重复

不检查目标槽位是否与以下内容冲突：

- 图标当前 `PixelPosition`
- `UnassignedIcons` 的当前位置
- 真实桌面上的新增图标
- 不属于当前模板的图标
- Explorer 自动对齐后的实际位置

不生成临时腾挪坐标、移动执行顺序、循环依赖解析、回滚操作、Writer 命令。

与真实桌面现状、移动路径、循环依赖、临时腾挪和 Writer 执行相关的碰撞处理属于 M2-04。

## Explorer 全局网格相位仍需真实机验证

M2-02C 的坐标计算基于数学网格（Zone 左上角 + 索引 × spacing）。
Explorer 可能使用略微不同的全局网格相位（受显示器 DPI、任务栏位置等因素影响）。
真实机验证待后续阶段。

## 防御性复制与模型不变量

### DesktopIconTargetPlacement

- 构造函数计算所有派生属性（`ColumnIndex`、`RowIndex`、`TargetPosition`、`TargetCellBounds`）
- 调用方只传入 `icon`、`zone`、`iconSpacing`、`slotIndex`
- `TargetCellBounds` 验证完全位于 `Zone.PixelBounds`
- `sealed record` — 默认值语义，get-only 属性

### DesktopLayoutZonePlacement

- 构造时对 `placements` 执行 `ToList()` + `AsReadOnly()`
- 验证 `SlotIndex` 严格等于列表索引 i：0,1,2,... 连续
- 验证每个 `Placement.Zone` 通过 `ReferenceEquals` 等于 `Assignment.Zone`
- 验证 `Icon` 身份逐项匹配 `Assignment.Icons[i]`
- 验证 `IconSpacing` 一致
- 验证 `TargetPosition` 不重复
- 验证 `TargetCellBounds` 不重叠
- 验证每 `TargetCellBounds` 完全位于 `Zone.PixelBounds`

### DesktopLayoutPlacementPlan

- 构造时对所有集合执行防御性复制 + `AsReadOnly()`
- 验证 `ZonePlacements.Count == AssignmentPlan.ZoneAssignments.Count`
- 验证每个 `ZonePlacement.Assignment` 通过 `ReferenceEquals` 匹配
- 验证 `IconSpacing` 与 `ResolvedTemplate.IconSpacing` 一致
- 拼接所有 Placements，验证数量等于 `AssignmentPlan.AssignedCount`
- 拼接的图标序列逐项与 `AssignmentPlan.ZoneAssignments` 拼接序列比较（Ordinal + StableId）
- 验证所有 `TargetCellBounds` 完全位于 `WorkArea`
- 验证所有 `TargetPosition` 全局唯一
- 防御性复制 `UnassignedIcons` 自 `AssignmentPlan`

## 不可达纵深防御

以下检查在生产代码中存在，但正常 API 调用路径不可命中：

- **非法 PlacementFlow**：`DesktopLayoutZone` 公共构造函数已拒绝非法枚举值；
  `DesktopIconTargetPlacement` 再次检查 Flow，属于纵深防御。
- **重复 TargetPosition**：连续 SlotIndex + 同一 Zone + 同一 spacing
  唯一推导 TargetPosition；`DesktopLayoutZonePlacement` 仍保留重复位置检查。
- 不使用反射或未初始化对象测试正常 API 不可达状态。

## 本阶段边界

**实现**：

- `DesktopIconTargetPlacement`（单图标目标放置）
- `DesktopLayoutZonePlacement`（单分区所有目标放置）
- `DesktopLayoutPlacementPlan`（完整坐标规划）
- `DesktopLayoutCoordinatePlanner`（静态 `Plan` 方法）
- 测试：Planner 37 + Models 36 = 73 tests
- 实验文档

**不实现**：

- WPF 预览
- 用户规则
- Writer
- 移动排序
- 循环依赖
- 临时腾挪
- 真实机验证
- Explorer 全局网格对齐校正
- 与真实桌面现状和移动路径相关的碰撞处理

## M2-03 如何消费 PlacementPlan

M2-03 可以：

- 读取 `PlacementPlan.Placements`（扁平化的目标坐标序列）
- 读取 `PlacementPlan.ZonePlacements`（按 Zone 组织的目标坐标）
- 使用 `TargetPosition` 作为 Writer 执行的目标坐标
- 使用 `TargetCellBounds` 进行碰撞检测
- 使用 `PlacementPlan.IsComplete` 决定是否允许执行

## M2-04 执行阶段如何消费 TargetPosition

M2-04 的 Writer 执行阶段可以：

- 接收 `DesktopLayoutPlacementPlan`
- 遍历 `Placements`，为每个图标生成移动命令
- 拒绝 `IsComplete == false` 的计划
- 根据 `SlotIndex` 和 `PlacementFlow` 推断移动执行顺序
- 在发现碰撞时决定临时腾挪策略
