# M1-05C-A：布局恢复安全执行协调器

## 阶段目标

将 M1-05B 生成的 `TargetDisplayLayoutExecutionPlan` 安全地逐步执行，包含重新规划、安全快照、Writer 调用、步骤验证与保守回滚。

## 为什么不能执行旧 ExecutionPlan

M1-05A 生成的计划可能在生成后被外部因素（用户操作、Explorer 重启等）陈旧化。M1-05C-A **始终**在 Writer 调用前：
1. 重新通过 STA 获取最新 capabilities
2. 重新读取完整桌面图标
3. 重新读取当前显示器拓扑
4. 调用 `TargetDisplayLayoutExecutionPlanner.CreatePlan` 重新生成执行计划

不接受直接传入旧 ExecutionPlan。

## 架构边界

- **Core**：`TargetDisplayLayoutExecutionRunStatus`（枚举）、`TargetDisplayLayoutExecutionRunResult`（结果）、`TargetDisplayLayoutRecoverySession`（恢复会话）
- **WindowsInterop**：`TargetDisplayLayoutExecutionCoordinator`（协调器）
- **Tests**：Fake/Scripted 测试替身 + 20+ 测试场景

WindowsInterop 只依赖 Core 接口，不依赖 DesktopAgent.Persistence。协调逻辑不在 MainWindow 中。

## 安全快照时序

1. 执行计划生成且可执行（非 NoOp）后
2. 调用 `DesktopLayoutSnapshotFactory.Create` 从当前桌面图标创建快照
3. 调用 `IDesktopLayoutSnapshotStore.SaveAsync` 保存
4. 调用 `IDesktopLayoutSnapshotStore.GetAsync` 回读
5. 逐字段验证回读快照与原始一致（SnapshotId、FormatVersion、TargetMonitorDeviceName、ConnectionMethod、Icons.Count、每个图标的全部字段）
6. 验证失败 → FailedBeforeWrite，不调用 Writer

安全快照暂时保留，不自动删除。

## 回读验证字段

- SnapshotId、FormatVersion
- TargetMonitorDeviceName（Ordinal）
- ConnectionMethod
- Icons.Count
- 每个图标：Ordinal, StableId (Ordinal), DisplayName, ParsingName, ItemKind, PixelPosition, MonitorDeviceName (Ordinal), IsSystemItem, IsFileSystemItem

## 恢复会话创建时机

在安全快照验证通过后、**第一次 Writer 调用前**创建 `TargetDisplayLayoutRecoverySession`：
- SessionId、SafetySnapshotId 均已确定
- 记录源恢复计划项和执行计划步骤的只读副本
- 初始状态：CompletedForwardSteps 和 CompletedRollbackSteps 为空

## 正向步骤验证

每一步：
1. 确认 `step.StepIndex` 连续
2. 确认 `step.FromPosition != step.ToPosition`
3. 调用 `IDesktopIconPositionWriter.MoveDesktopIcon`
4. 验证返回 `StableId` Ordinal 相同
5. 验证 `PreviousPosition == step.FromPosition`
6. 验证 `ActualPosition == step.ToPosition`（IsExact）

任何一步失败 → 停止正向、尝试回滚。

## 逆序回滚

按已成功正向步骤的逆序执行：
`rollbackTarget = forwardStep.FromPosition`

例如正向：A→staging, B→target, A→target
回滚：A→staging, B→original, A→original

每个回滚结果验证 StableId、PreviousPosition、IsExact。

## 回滚停止条件

- 正向 Writer 抛异常且无法确认实际状态
- 回滚 Writer 抛异常
- PreviousPosition 与预期不一致
- ActualPosition 与 rollbackTarget 不一致
- StableId 不一致

触发条件 → RequiresManualRecovery=true, IsPositionUncertain=true, 保留 SafetySnapshotId。

## 不确定状态处理

- `PositionUncertain`：正向 Writer 异常或回滚失败
- `RequiresManualRecovery = true`
- `IsPositionUncertain = true`
- 安全快照保留，Session 记录已完成的步骤
- 不宣称已恢复

## Fake Writer 测试边界

`ScriptedDesktopIconPositionWriter` 使用 FIFO 队列消费脚本：
- 精确结果、非精确结果
- 错误 StableId、错误 PreviousPosition
- 指定步骤/回滚抛异常

测试不实例化 `DesktopShellIconPositionWriter`。

## M1-05C-B 将负责

- 真实 PoC 集成
- WPF 按钮接入
- 真实 `DesktopShellIconPositionWriter` 调用
- 实机移动验证
