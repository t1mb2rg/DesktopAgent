# M1-05B：指定显示器布局恢复执行步骤规划器

## 阶段目标

将 M1-05A 生成的 `TargetDisplayLayoutRestorePlan` 转换为确定性的原子移动步骤序列 `TargetDisplayLayoutExecutionPlan`，包含直接移动、临时中转和最终目标移动。

## 为什么 M1-05B 仍不调用 Writer

执行计划是纯逻辑产物。M1-05B 在没有任何 Shell COM 调用的前提下，通过内存模拟验证完整移动序列的可执行性。Writer 调用留到 M1-05C。

## 为什么需要完整当前桌面作为输入

M1-05A 生成的计划可能在生成后被外部因素（用户操作、Explorer 重启等）陈旧化。M1-05B 需要独立验证每个计划项对应的当前状态是否与计划中的假设一致。

## 原子步骤模型

三种步骤类型：
- `MoveToTarget`：从当前位置直接移动到快照目标
- `MoveToTemporaryStaging`：移动到临时中转位置
- `MoveFromTemporaryStagingToTarget`：从中转位置移动到快照目标

`TargetDisplayLayoutExecutionStep` 公开字段：`StepIndex`, `StableId`, `DisplayName`, `FromPosition`, `ToPosition`, `Kind`, `ExpectedMonitorDeviceName`, `StagingSequence`。已删除未使用的 `RelatedCycleStableIds`。

## 内存占用模拟

维护所有当前图标的坐标映射和占用矩形。每生成一步后更新模拟状态。后续所有直接移动判定和中转位置搜索都基于当前模拟状态。

## 临时中转位置搜索

网格扫描从 workArea 左上角开始，步长 = spacing，使用 `long` 防止 int 溢出。候选必须：
- 不与任何模拟占用重叠（排除自身）
- 不与任何最终目标矩形重叠
- 完整包含在 WorkArea 内
- **不等于当前图标位置**（防止生成 `FromPosition == ToPosition` 的步骤）

## 动态释放和复用

前一个中转位置在图标离开后立即释放，后续环可以复用。不需要预先分配或预先保留专属中转位置。StagingAssignment 使用 `Dictionary<string, int>` 索引表更新，确保唯一更新和确定性。

## 路径分派：无环与有环独立路径

根据 `RequiresTemporaryStaging` 分派：

### 无环路径（`RequiresTemporaryStaging == false` → `ExecuteAcyclicPath`）

- 严格遵循 `MoveExecutionOrder`
- 只执行顺序中的下一项，不允许跳过，不允许重排
- 目标被占用时返回失败，永不生成 `MoveToTemporaryStaging`

### 有环路径（`RequiresTemporaryStaging == true` → `ExecuteCyclicPath`）

- `MoveExecutionOrder` 必须为 null，`Cycles` 至少包含一个真实环
- 进入通用 staging 模拟循环

## MoveExecutionOrder 精确排列验证

在无环路径入口按序检查：
1. 非 null
2. 无重复（`HashSet<string>(StringComparer.Ordinal)` 去重）
3. 数量等于 `MoveRequiredCount`
4. 不含额外项（不在 `moveSidSet` 中的 StableId）
5. 隐式保证无遗漏（集合大小相等 + 子集关系）

所有比较使用 `StringComparison.Ordinal`。

## 源计划摘要重算

从 `restorePlan.Items` 重新计算并逐一比较：
- `SnapshotTargetIconCount`（通过 `Items.Count`）
- `AlreadyAtTargetCount`
- `MoveRequiredCount`
- `MissingCount`
- `BlockedCount`
- `HasBlockingErrors`（标志位一致性）
- `IsNoOp`（从 `recalcMove == 0 && recalcMissing == 0 && recalcBlocked == 0` 推导）

不一致返回 `SourcePlanInconsistent`，Message 包含具体字段名。

## 项目状态内部一致性

### AlreadyAtTarget 约束

- `CurrentPosition` 非 null
- `CurrentMonitorDeviceName` 非 null
- `CurrentPosition == SnapshotTargetPosition`
- `CurrentMonitorDeviceName` 与 `TargetDeviceName` Ordinal 相同

### MoveRequired 约束

- `CurrentPosition` 非 null
- `CurrentMonitorDeviceName` 非 null
- 不能同时已经位于目标坐标和目标显示器

### MissingFromCurrent

- 当前桌面中必须不存在（由陈旧性检查验证）

若 `HasBlockingErrors == false`，只允许上述三种状态。存在阻断状态但标志为 false → `SourcePlanInconsistent`。

## staging 安全

- staging 不得等于当前位置（`FindStagingPosition` 中 `candidate == currentPosition` 跳过）
- 每个执行步骤 `FromPosition != ToPosition`（`EveryExecutionStep_ChangesPosition` 测试覆盖）
- staging 避开当前模拟占用和全部最终目标矩形

## 公共集合不可变

`TargetDisplayLayoutExecutionPlan` 构造函数：

```csharp
Steps = Array.AsReadOnly(steps.ToArray());
StagingAssignments = Array.AsReadOnly(stagingAssignments.ToArray());
Issues = Array.AsReadOnly(issues.ToArray());
```

使用 `Array.AsReadOnly<T>()` 返回 `ReadOnlyCollection<T>`，调用方无法通过 `IList<T>` 强制转换或数组强制转换修改。尝试 `IList<T>.Add`、`Remove`、`Clear`、索引设置均抛出 `NotSupportedException`。

计数字段从复制后的 `Steps` 计算，不与构造函数参数耦合。

## 失败原子性

任何检查失败、模拟死锁或验证不通过时，`IsExecutable = false` 且 `Steps`、`StagingAssignments` 均为空。内部即使已经生成临时步骤，最终公开结果完全干净——不返回部分步骤。

## 清理

- 删除未使用的 `extraIcons` 变量
- 避免 `catch (Exception)` 捕获 `UniqueDisplaySelector`，只捕获 `InvalidOperationException` 和 `ArgumentException`
- StagingAssignment 更新使用 `Dictionary<string, int>` 索引表替代 `First()` 查找
- 删除未使用的 `RelatedCycleStableIds` 字段

## M1-05C 将负责

- 调用 `DesktopShellIconPositionWriter.MoveDesktopIcon`
- 每步执行后回读实际位置
- 处理 Writer 异常与回滚
- 实机验证
