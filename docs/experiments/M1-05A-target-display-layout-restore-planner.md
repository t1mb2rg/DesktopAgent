# M1-05A：指定显示器布局恢复规划器

## 阶段目标

在 M1-03A（快照持久化）和 M1-04A（单图标移动后端）基础上，实现指定显示器布局恢复的纯逻辑规划器。M1-05A 只生成计划，不执行计划。

## 为什么批量恢复前必须先生成计划

批量图标恢复涉及多个图标的移动、依赖关系和冲突检测。直接执行可能导致图标互相阻塞、部分移动后无法完成后续移动。生成计划相当于一个可在执行前完全审计的"试运行"。

## 为什么 M1 仅恢复指定显示器

M1 的恢复目标是逐步、可验证的。整桌面恢复涉及跨显示器搬迁、负坐标处理、未知 StableId 边界等复杂问题。M1-05A 将范围限制在单显示器。

## 一个快照目标对应一个计划项

不变量：`plan.Items.Count == plan.SnapshotTargetIconCount`。包括快照重复、缺失和阻断项，目标集合为空时二者均为 0。

## 快照级验证顺序

重复 StableId（阶段 3）和重复目标坐标（阶段 4）必须在任何当前桌面匹配之前完成。每个重复条目都产生独立计划项并保留完整诊断信息。

## 当前桌面全局安全校验

阶段 5 对完整当前桌面执行两项全局安全检查：

- **StableId 重复**：任何 StableId 出现次数 > 1 时生成 `DuplicateCurrentStableId` Issue，即使该 StableId 不属于快照目标集合。
- **矩形溢出**：任何当前图标坐标计算占用矩形时发生 checked 算术溢出时生成 `CurrentRectangleOverflow` Issue。

两项结果通过 `Plan.Issues` 表达。

## 空目标集合

即使快照目标集合为空，也必须先完成全局当前桌面校验：

- 无阻断 Issue → `IsNoOp = true`，`HasBlockingErrors = false`
- 有阻断 Issue → `IsNoOp = false`，`HasBlockingErrors = true`，`Issues` 完整保留

## 共享半开矩形语义

`DesktopIconMovePlanner` 和 `TargetDisplayLayoutRestorePlanner` 统一使用 `DesktopIconLayoutGeometry`：

| 方法 | 语义 |
|---|---|
| `CreateRectChecked(pos, spacing)` | checked 创建 `[pos, pos+spacing)` |
| `TryCreateRect(pos, spacing, out rect)` | 溢出时返回 false |
| `Overlaps(a, b)` | `a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top` |
| `IsFullyContained(inner, outer)` | inner 四边均在 outer 内 |

## 全量占用者扫描

阶段 8 遍历全部当前图标（排除唯一匹配的自身 CurrentIconIndex），收集所有与目标矩形重叠的占用者。不找到第一个后停止。

## AlreadyAtTarget

必须同时满足两个条件：

1. `currentIcon.PixelPosition == snapIcon.PixelPosition`
2.  `string.Equals(currentIcon.MonitorDeviceName, targetDeviceName, StringComparison.Ordinal)`

AlreadyAtTarget 是静态占用者，不会为了其他图标让位。目标被 AlreadyAtTarget 占用时动作项标记 `BlockedByStationaryIcon`，不建立依赖。

## 阻断固定点传播

阶段 9 使用 `while (changed)` 循环反复传播阻断直到无新阻断。传播规则：

- 目标被已就位图标占用 → 阻断
- 目标被非计划图标占用 → 阻断
- 目标被传播后已阻断的项占用 → 阻断
- 只有最终仍为 MoveRequired 的占用者才建立依赖

## 最终依赖

阶段 10 仅对双方最终均为 `MoveRequired` 的占用关系生成依赖。已阻断项、已就位项、快照重复项均不参与依赖。

## Tarjan SCC

阶段 11 使用 Tarjan 强连通分量算法：

- SCC 节点数 > 1 或自环 → 环
- 环后节点不属于环，不在 Cycle 中
- 两个不相交的环分别输出
- `RequiresTemporaryStaging = true` 当至少存在一个真实环

## 确定性顺序

- 每个 `Cycle.StableIds` 按 `StringComparer.Ordinal` 排序
- `Cycles` 列表按环内第一个 StableId 的 Ordinal 排序
- `MoveExecutionOrder` 使用 `SortedSet` + 快照索引优先 + StableId Ordinal 兜底
- 存在阻断或环时 `MoveExecutionOrder = null`

## checked 溢出

- 目标矩形计算溢出 → 对应计划项标记 `TargetOutsideCurrentWorkArea`
- 当前图标矩形计算溢出 → 生成 `CurrentRectangleOverflow` 计划级 Issue

## 阶段边界

M1-05A：

- 不调用 Writer
- 不调用 SelectAndPositionItems
- 不寻找临时中转位置
- 不执行批量恢复
- 所有代码均为纯逻辑 + 自动化测试覆盖

## M1-05B 将负责

- 使用本阶段的计划执行安全移动序列
- 遇到静态阻断或环时回滚或终止
- 实现临时中转位置分配与执行
