# M1-05C-B：受控指定显示器布局恢复 PoC

## 阶段目标

在现有 WPF PoC 中增加独立的"指定显示器布局恢复"受控入口，保留 M1-04B 单图标移动功能。

## 单图标、单步骤、无中转安全闸门

`TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove` 验证 20 个条件，返回所有拒绝原因：

- HasBlockingErrors=false, MissingCount=0, BlockedCount=0, MoveRequiredCount=1, IsNoOp=false
- 恰好一个 MoveRequired item：IsFileSystemItem=true, IsSystemItem=false, CurrentPosition non-null
- ExecutionPlan: IsExecutable=true, IsNoOp=false, UsesTemporaryStaging=false, Steps.Count=1
- Step: StepIndex=0, Kind=MoveToTarget, StableId 与 item 中 Ordinal 相同
- Step.FromPosition == item.CurrentPosition, Step.ToPosition == item.SnapshotTargetPosition
- TargetDeviceName 一致, FromPosition != ToPosition

## 三层验证边界

### 第一层：预览阶段闸门
在预览流程中，使用预览时刻的 RestorePlan 和 ExecutionPlan 运行 `EvaluateSingleDirectMove`。
结果展示在诊断输出中，用于告知用户是否满足单图标、单步骤、无中转条件。

### 第二层：点击执行时预览状态复检
点击"执行一次受控恢复"时，对缓存的预览计划再次运行闸门，防止预览后至点击前状态变化。

### 第三层：Coordinator 内部最新重规划后的最终写入闸门
`ExecuteWithScopeGateAsync` 在 Coordinator 内部：
1. 重新读取桌面状态、重新生成 ExecutionPlan
2. 结构验证通过后
3. **调用 `EvaluateSingleDirectMove(restorePlan, executionPlan)`**
4. 只有 `IsAllowed == true` 才进入安全快照保存和 Writer 调用

如果最新计划不通过：
- Status = `FailedBeforeWrite`
- ForwardFailureType = `"ExecutionScopeRejected"`
- **不保存安全快照**（SafetySnapshotId = null）
- **不创建 Session**
- **不调用 Writer**
- 用户必须重新预览

## 预览与真实执行的重新规划边界

**预览**：从不调用 Writer。
- 重新读取显示器拓扑、capabilities、桌面图标
- 使用 DisplayAssignmentService 分配显示器
- 调用 RestorePlanner + ExecutionPlanner + ScopeGate
- 展示完整预览诊断

**真实执行**：只调用 Coordinator 的 `ExecuteWithScopeGateAsync`。
- MainWindow 重新运行闸门（不信任预览状态）
- Coordinator 内部再次读取、规划 → 闸门 → 保存快照 → 执行 → 验证
- Coordinator 内部闸门是最终写入授权边界

## 双重确认

1. 同意框：必须勾选明确的文字确认
2. MessageBox：展示 SnapshotId、图标信息、From/To、目标显示器，默认 No

## SafetySnapshot 和 RecoverySession

- SafetySnapshot 保留，不自动删除
- RecoverySession 深度冻结完整 SourceRestorePlan 和 ExecutionPlan
- CompletedForwardStepIndexes / CompletedRollbackSourceStepIndexes

## 状态分类与 UI 处理

| 状态 | UI |
|---|---|
| Completed / CompletedNoOp | 清除预览和同意框 |
| FailedBeforeWrite | Writer 未开始，无副作用 |
| FailedAndRolledBack | 正向失败但已确认回滚 |
| FailedRollbackIncomplete | 设置人工处理锁 |
| PositionUncertain | 设置人工处理锁 |

## 人工处理锁

- `_layoutRestoreRequiresAttention = true`
- 禁止继续真实写入、禁止 M1-04B 移动
- 保留结果、Session 和 SafetySnapshotId
- 关闭时二次确认

## 首次实机预览发现（2026-07-26）

### 环境

- 目标显示器：`\\.\DISPLAY2`，工作区 `(124,0)-(2560,1440)`
- 基准快照：`7e7a4f6d-4444-48af-b451-5e98f903f0fb`
- 测试文件夹：`DesktopAgent-M1-05C-B-Test`，基准 `(1886,507)`，移动后 `(1962,507)` → 正确识别为 MoveRequired

### 问题

边缘图标「完美世界竞技平台」：
- `CurrentPosition=(2494,1315)`，`SnapshotTargetPosition=(2494,1315)`，`Monitor=\\.\DISPLAY2`
- 占用矩形 `(2494,1315)-(2574,1395)` 右边界 `2574 > 2560`（工作区右边界）

RestorePlanner 阶段 7 在检查 `AlreadyAtTarget` 之前先执行了 `IsFullyContained(targetRect, WorkArea)`，导致该 AlreadyAtTarget 图标被错误分类为 `TargetOutsideCurrentWorkArea`，阻断了 `ExecutionPlan.IsExecutable` = false。

- Writer 未启动
- PR 转回 Draft

### 修复

将 AlreadyAtTarget 判断（坐标相等 + 显示器 Ordinal 匹配）**提前**到目标工作区验证之前。

原则：
- 仅 AlreadyAtTarget 项目绕过目标工作区写入验证（不需要 Writer）
- MoveRequired 项目仍严格验证目标占用矩形完全位于工作区
- 其他显示器同坐标项目仍必须通过工作区验证（不能绕过）

## 最终受控实机验收（2026-07-26）

### 环境

- 目标显示器：`\\.\DISPLAY2`
- 测试对象：`DesktopAgent-M1-05C-B-Test`
- 基准快照：`7e7a4f6d-4444-48af-b451-5e98f903f0fb`
- 基准位置：`(1886,507)`
- 手工移动后位置：`(1962,507)`

### 修复后预览

| 字段 | 值 |
|---|---|
| SnapshotTargetIconCount | 43 |
| AlreadyAtTargetCount | 42 |
| MoveRequiredCount | 1 |
| MissingCount | 0 |
| BlockedCount | 0 |
| HasBlockingErrors | false |
| RequiresTemporaryStaging | false |
| ExecutionPlan.IsExecutable | true |
| ExecutionPlan.Steps.Count | 1 |
| UsesTemporaryStaging | false |
| Gate.IsAllowed | true |

### 唯一执行步骤

| 字段 | 值 |
|---|---|
| Kind | MoveToTarget |
| StableId | `c:\users\example-user\desktop\desktopagent-m1-05c-b-test` |
| FromPosition | `(1962,507)` |
| ToPosition | `(1886,507)` |
| TargetDeviceName | `\\.\DISPLAY2` |

### 真实执行结果

| 字段 | 值 |
|---|---|
| Status | Completed |
| IsSuccess | true |
| IsNoOp | false |
| SafetySnapshotId | `8b6ab498-4b98-494d-b788-ab7b85ee0840` |
| SessionId | `b6362dcc-9d6a-4529-b42e-cbb04413ec8d` |
| RequiresManualRecovery | false |
| IsPositionUncertain | false |
| ForwardAttempts.Count | 1 |
| RollbackAttempts.Count | 0 |

### Forward Attempt

| 字段 | 值 |
|---|---|
| Phase | Forward |
| Outcome | ExactSuccess |
| ConnectionMethod | FindWindowSW |
| ExpectedPreviousPosition | `(1962,507)` |
| ReportedPreviousPosition | `(1962,507)` |
| RequestedPosition | `(1886,507)` |
| ActualPosition | `(1886,507)` |
| IsExact | true |
| HasTrustedActualPosition | true |
| Writer Exception | none |
| PrimaryPath Exception | none |

### 独立重新读取

| 字段 | 值 |
|---|---|
| 图标总数 | 118 |
| 失败数 | 0 |
| ConnectionMethod | FindWindowSW |
| 测试文件夹最终坐标 | `(1886,507)` |
| 所属显示器 | `\\.\DISPLAY2` |
| IsFileSystemItem | true |
| IsSystemItem | false |

### 验收结论

M1-05C-B 的单图标、单步骤、无中转受控恢复闭环通过。
Coordinator 完成最新读取、重新规划、最终 ScopeGate、
安全快照、RecoverySession、真实写入和精确回读；
独立重新读取再次确认最终位置。

## 未运行 PoC 的代码提交边界

本轮代码实现完成：
- ScopeGate + 11 个测试
- DI 注册 Coordinator + SafetySnapshotFactory
- MainWindow TabControl + 预览/执行逻辑
- Window 关闭保护
- AlreadyAtTarget 边缘图标修复 + 4 个测试

M1-05C-B 代码可在**不运行 PoC** 的条件下提交。

## 实机验收步骤与结果

以下步骤已于 2026-07-26 完成，详见上方「最终受控实机验收」。

1. 在目标显示器桌面创建临时文件夹：`DesktopAgent-M1-05C-B-Test` ✅
2. 关闭「自动排列图标」 ✅
3. 读取桌面并保存快照 ✅
4. 记录 SnapshotId ✅（`7e7a4f6d-4444-48af-b451-5e98f903f0fb`）
5. 手工拖动该临时文件夹一个网格位置 ✅（`(1886,507)` → `(1962,507)`）
6. 不移动其他图标 ✅
7. 选择刚保存的快照 ✅
8. 预览满足：MoveRequiredCount=1, Steps.Count=1, MoveToTarget, UsesTemporaryStaging=false ✅
9. 二次确认并执行 ✅
10. Status=Completed, ForwardAttempts.Count=1, Outcome=ExactSuccess, RollbackAttempts.Count=0,
    RequiresManualRecovery=false, IsPositionUncertain=false ✅
11. 重新读取桌面 ✅
12. 确认临时文件夹回到原位置 ✅（`(1886,507)`，`\\.\DISPLAY2`）
13. 记录 SafetySnapshotId、SessionId、实际坐标 ✅
