# M1-05C-A：布局恢复安全执行协调器

## 阶段目标

将 M1-05B 生成的 `TargetDisplayLayoutRestorePlan` 安全地逐步执行，包含重新规划、显示器分配、安全快照、Writer 调用、步骤验证与保守回滚。

## 阶段边界

- 未接入 WPF
- 未连接 Explorer
- 未实例化 `DesktopShellIconPositionWriter`
- 未调用 `SelectAndPositionItems`
- 安全快照保留，不自动删除
- M1-05C-B 才进行受控实机 PoC

## 架构边界

- **Core**：`TargetDisplayLayoutExecutionRunStatus`、`TargetDisplayLayoutExecutionRunResult`、`TargetDisplayLayoutRecoverySession`、`TargetDisplayLayoutExecutionAttemptResult`、`TargetDisplayLayoutExecutionAttemptPhase`、`TargetDisplayLayoutExecutionAttemptOutcome`
- **WindowsInterop**：`TargetDisplayLayoutExecutionCoordinator`、`IDesktopLayoutSafetySnapshotFactory`、`DefaultSafetySnapshotFactory`
- **Tests**：Fake/InMemory/Scripted 测试替身 + 41 个 [Fact] 测试

WindowsInterop 只依赖 Core 接口。Coordinator 通过 `internal` 构造函数接受可选的 Planner 委托和快照工厂抽象。

## 最新桌面读取与显示器归属

真实 `DesktopShellIconReader` 返回的 `DesktopIconDescriptor.MonitorDeviceName` 可能为 null。

Coordinator 读取原始图标后调用 `DisplayAssignmentService.AssignMonitor(icon.PixelPosition, currentDisplays)`，
创建 `assignedCurrentRead`（`DesktopIconReadResult`）。

后续所有阶段使用 `assignedCurrentRead`：
- `ExecutionPlanner.CreatePlan`
- `DesktopLayoutSnapshotFactory.Create`（安全快照）
- 任何显示器归属验证

## Writer 前准备阶段（统一异常边界）

| 阶段 | 异常处理 |
|---|---|
| GetCapabilities / ReadDesktopIcons / AssignMonitor | catch OCE → throw; catch Ex → FailedBeforeWrite |
| CreatePlan | catch OCE → throw; catch Ex → FailedBeforeWrite |
| ExecutionPlan 结构验证 | FailedBeforeWrite（不保存快照） |
| SnapshotFactory.Create | catch OCE → throw; catch Ex → FailedBeforeWrite |
| SnapshotStore.SaveAsync | catch OCE → throw; catch Ex → FailedBeforeWrite |
| SnapshotStore.GetAsync | catch OCE → throw; catch Ex → FailedBeforeWrite（保留 SafetySnapshotId） |
| readBack null | FailedBeforeWrite（保留 SafetySnapshotId） |
| VerifySafetySnapshot | FailedBeforeWrite（保留 SafetySnapshotId） |

- Save 成功后的回读失败保留 SafetySnapshotId（数据库中已存在）
- Save 自身失败 SafetySnapshotId 为 null

## ExecutionPlan 结构验证

在保存安全快照前由 `ValidateExecutionPlanStructure`（`internal static`）验证：

- `IsExecutable == true`
- `IsNoOp == false`
- `Steps.Count > 0`
- `StepIndex` 严格 `0..Count-1`
- `StableId` 非 null、非空白
- `FromPosition != ToPosition`

## cancellation 安全边界

| 阶段 | 行为 |
|---|---|
| Writer 开始前 | `ThrowIfCancellationRequested()` — 无副作用，正常取消 |
| Writer 调用中 | 不使用 `WaitAsync(token)`，必须等待底层同步调用结束 |
| 步骤间取消（有已知步骤） | 回滚已知精确步骤，返回 FailedAndRolledBack |
| 回滚期间 | 忽略外部取消，必须完成或明确失败 |
| 最后一步精确完成后取消 | 返回 Completed |

正向和回滚 MoveDesktopIcon 调用均不使用 `.WaitAsync(cancellationToken)`。

## 正向失败分类

| 结果 | 状态 | 自动回滚 |
|---|---|---|
| **WriterException**（Writer 抛异常） | PositionUncertain | 禁止 |
| **StableIdMismatch**（返回错误 StableId） | PositionUncertain | 禁止 |
| **PreviousPositionMismatch**（前置位置不匹配） | PositionUncertain | 禁止 |
| **InexactKnown**（StableId/PreviousPosition 可信，ActualPosition 已知） | 触发回滚 | **允许** |

只有 **InexactKnown**（StableId 和 PreviousPosition 均可信、ActualPosition 已知但不等于目标）才允许自动回滚。

Writer 异常、StableId mismatch、PreviousPosition mismatch 一律返回 PositionUncertain，禁止自动回滚。

## 保守回滚（仅 InexactKnown 触发）

按已成功正向步骤的逆序执行，先回滚当前非精确步骤，再回滚之前步骤。

回滚验证顺序（每步先验证身份再判断位置）：

1. Writer 返回
2. 验证 Reported StableId
3. 验证 PreviousPosition
4. 身份和前置状态可信后，才判断 ActualPosition 精确性

## 回滚失败分类

| 结果 | 状态 | IsPositionUncertain |
|---|---|---|
| WriterException | PositionUncertain | true |
| RollbackStableIdMismatch | PositionUncertain | true |
| RollbackPreviousPositionMismatch | PositionUncertain | true |
| RollbackInexact（已知非精确） | FailedRollbackIncomplete | **false** |

- `FailedRollbackIncomplete`：位置已知但不精确，`RequiresManualRecovery=true`，`IsPositionUncertain=false`
- 回滚 mismatch 不进入 `CompletedRollbackSourceStepIndexes`
- 之前已精确完成的回滚步骤会保留在 CompletedRollback 中

## AttemptResult（不含 Exception 对象）

`TargetDisplayLayoutExecutionAttemptResult` 公开字段：
`AttemptIndex`, `SourceStepIndex`, `Phase`（Forward/Rollback）, `ExpectedStableId`, `ExpectedPreviousPosition`, `RequestedPosition`, `ReportedStableId`, `ReportedPreviousPosition`, `ActualPosition`, `HasTrustedActualPosition`, `IsExact`, `Outcome`（ExactSuccess/InexactKnown/WriterException/StableIdMismatch/PreviousPositionMismatch）, `ConnectionMethod`, `ExceptionType`, `ErrorMessage`, `PrimaryPathExceptionType`, `PrimaryPathExceptionMessage`

- 不含 `Exception` 对象
- mismatch 的 `HasTrustedActualPosition` 为 false，不标记为 ExactSuccess

## RecoverySession

保存完整 `SourceRestorePlan`（`TargetDisplayLayoutRestorePlan`）和 `ExecutionPlan`（`TargetDisplayLayoutExecutionPlan`）。

深度冻结：
- Items、Dependencies、Issues、MoveExecutionOrder → `Array.AsReadOnly`
- 每个 `Cycle.StableIds` → `Array.AsReadOnly`

`CompletedForwardStepIndexes`：仅 ExactSuccess 的 SourceStepIndex
`CompletedRollbackSourceStepIndexes`：使用原执行步骤的 SourceStepIndex，按回滚执行顺序

## 安全快照时序

1. 执行计划生成且可执行（非 NoOp）后
2. 结构验证通过
3. `SnapshotFactory.Create` 从已分配显示器的图标创建
4. `SaveAsync` 保存
5. `GetAsync` 回读
6. 逐字段验证（SnapshotId、FormatVersion、TargetMonitorDeviceName、ConnectionMethod、Icons.Count、每个图标的 9 个字段）
7. 验证失败 → FailedBeforeWrite，不调用 Writer

## 测试边界

- `ScriptedDesktopIconPositionWriter`：FIFO 队列 + 阻塞脚本（`TaskCompletionSource`）+ 目标位置验证 + `AttemptHistory`（含异常调用）
- `FakeDisplayTopologyReader`、`FakeDesktopIconReader`、`InMemoryDesktopLayoutSnapshotStore`（含 SaveCount）
- `ThrowingSnapshotFactory`：可注入快照工厂异常
- Planner 委托注入：可测试 CreatePlan 异常转换
- 协调器测试数量：**41 个** [Fact]

## M1-05C-B 将负责

- 真实 PoC 集成
- WPF 按钮接入
- 真实 `DesktopShellIconPositionWriter` 调用
- 实机移动验证
