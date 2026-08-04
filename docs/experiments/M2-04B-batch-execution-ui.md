# M2-04B：无中转批量桌面整理执行

## 阶段目标

在 LayoutPreviewWindow 中完成一次用户可见的完整闭环：

选择模板和目标显示器
→ fresh re-read 模板与规则
→ fresh read 拓扑、Capabilities 和桌面图标
→ 规则感知 PreviewPlan
→ ExecutionCandidate
→ 展示移动、步骤和风险
→ 用户显式同意
→ 二次确认
→ 再次完整重读并重建最新 Candidate
→ 与用户确认的 Candidate 做语义一致性比较
→ Coordinator 再次重读并重新规划
→ 无中转批量 ScopeGate
→ 保存并回读安全快照
→ 批量 Writer 执行
→ 独立完整重读验证
→ Completed / FailedAndRolledBack / FailedRollbackIncomplete / PositionUncertain
→ 在 WPF 中展示完整结果

## 用户完整链路

```
选择模板和目标显示器 (已有)
→ 生成规则感知预览 (已有，M2-03C-B)
→ 自动生成 ExecutionCandidate (M2-04B 新增)
→ 查看候选摘要：移动数量、步骤、风险、中转状态 (新增)
→ 勾选同意框 (新增)
→ 点击"执行无中转批量整理" (新增)
→ 二次确认对话框 (新增)
→ Fresh re-read + rebuild + semantic compare (新增)
→ Coordinator 执行 (M1 已有，接入新批量 Gate)
→ 独立最终验证 (新增)
→ 展示执行结果、验证结果 (新增)
→ 人工处理锁传回 MainWindow (新增)
```

## Preview → Candidate → fresh rebuild → semantic compare → Coordinator

三层保护设计：

1. **用户确认层**：用户看到并确认的 Candidate（PreviewPlan + 数据）
2. **重建验证层**：点击执行后完全重新读取所有数据，重建 Candidate，语义比较
3. **Coordinator 层**：Coordinator Writer 前再次重读、重规划和 ScopeGate

语义比较器 `DesktopLayoutRuleAwareExecutionCandidateSemanticComparer`：
- 纯 Core 逻辑，可测试
- 覆盖 TemplateId、Version、TargetDeviceName、IconSpacing、当前图标元数据/位置/显示器、TargetSnapshot、RestorePlan Items 状态/位置/阻断、ExecutionPlan 状态和步骤
- 忽略 SnapshotId、CreatedAtUtc、对象引用
- 返回 `SemanticComparisonResult { IsEquivalent, Differences }`

## 无中转批量 Gate

`TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch()`：

- Fail-closed，一次返回所有拒绝原因
- 允许多图标、DAG 依赖链、多个 MoveToTarget 步骤
- 禁止循环、staging、非文件系统项、系统项、阻断/缺失项
- 逐 Step 验证 StepIndex 连续、Kind=MoveToTarget、StableId 唯一、From/To 与 RestoreItem 一致
- 保留原有的 `EvaluateSingleDirectMove`（语义和测试不变）

## 为什么 staging 留到 M2-04C

临时中转（Temporary Staging）用于循环交换等复杂场景：
- `MoveToTemporaryStaging` 和 `MoveFromTemporaryStagingToTarget` 步骤
- 中转位置搜索算法
- 循环依赖检测（Tarjan SCC）

M2-04B 聚焦于无中转的常见整理场景（单图标、多图标独立、DAG 依赖链）。
M2-04C 将开放 staging 的真实执行，同时引入 RecoverySession 持久化和跨重启恢复。

## 安全快照

仍然使用 M1 的完整安全快照链路：
- Coordinator 在 Writer 前创建并保存安全快照
- 回读验证快照完整性
- 快照覆盖完整桌面（所有显示器）

## Forward / Rollback

复用 M1 的逐步执行和回滚逻辑：
- Forward 逐步执行，每个 Writer 调用后验证 IsExact、StableId、PreviousPosition
- 非精确或异常时自动回滚
- 回滚后位置不确定时禁止盲目回滚

M2-04B 新增（见下节）。

## 独立最终验证

Coordinator 新增结构化的执行后独立完整重读验证：

**Forward 验证**：
- Forward 全部 ExactSuccess 后，独立 Shell 重读
- 验证 FailedItemCount=0、StableId 唯一、所有目标项位置和显示器正确
- 通过 → Completed
- 失败/不确定 → PositionUncertain

**Rollback 验证**：
- Rollback 全部 ExactSuccess 后，独立 Shell 重读
- 验证所有被移动图标已回到原始 FromPosition
- 通过 → FailedAndRolledBack
- 确定为未恢复 → FailedRollbackIncomplete
- 不确定 → PositionUncertain

**RunResult 新增字段**：
- `VerificationResult: TargetDisplayLayoutExecutionVerificationResult?`
  - `Phase`: Forward / Rollback
  - `Status`: Passed / Failed / Uncertain
  - `FailedItemCount`, `Issues`, `VerifiedAtUtc`

验证使用不可取消路径（`CancellationToken.None`），避免 UI 取消跳过验证。
Writer 等待边界不使用 `WaitAsync(token)`。

## 人工处理锁

LayoutPreviewWindow 关闭后，通过公开属性传回 MainWindow：
- `RequiresManualRecovery` → `_layoutRestoreRequiresAttention`
- `LastExecutionResult` → `_lastLayoutExecutionResult`
- `EscapedException` → `_layoutRestoreEscapedException`

MainWindow 同步禁用旧的单图标移动和快照恢复入口。
本阶段不持久化该锁；跨重启持久化属于 M2-04C。

窗口执行期间禁止关闭。

## 测试结果

- 新增：40 个（Batch ScopeGate 19 + 语义比较器 12 + Coordinator 验证 12 - 3 重复覆盖）
- 全量：1216/1216 通过
- 0 失败

### A. 无中转批量 Gate 测试（19 个）
1. 单图标直接移动允许
2. 多个互不依赖 MoveToTarget 允许
3. 无中转 DAG 依赖链允许
4. NoOp 拒绝
5. MoveRequiredCount == 0 拒绝
6. Missing 拒绝
7. Blocked 拒绝
8. system item 拒绝
9. non-file-system 拒绝
10. UsesTemporaryStaging 拒绝
11. StagingAssignments 非空拒绝
12. MoveToTemporaryStaging 拒绝
13. MoveFromTemporaryStagingToTarget 拒绝
14. Step 数量不匹配拒绝
15. StepIndex 不连续拒绝
16. StableId 重复拒绝
17. From/To 不一致拒绝
18. 目标显示器不一致拒绝
19. 现有 EvaluateSingleDirectMove 测试全部保持通过

### B. Candidate 语义比较测试（12 个）
1. 同语义不同对象实例等价
2. SnapshotId / CreatedAtUtc 变化仍等价
3. 当前位置变化不等价
4. 元数据变化不等价
5. TemplateId/Version 变化不等价
6. TargetDeviceName 变化不等价
7. IconSpacing 变化不等价
8. Restore Item 状态变化不等价
9. Step 字段变化不等价
10. 中转状态变化不等价
11. Differences 有可读信息

### C. Coordinator 验证测试（12 个）
1. 多步骤全部 Exact + 独立读回完全匹配 → Completed
2. 最终读取不完整 → PositionUncertain
3. 读取异常 → PositionUncertain
4. StableId 缺失 → PositionUncertain
5. StableId 重复 → PositionUncertain
6. 最终位置不匹配 → PositionUncertain
7. 两步全部 Exact + 验证通过 → Completed
8. 回滚后读回原位置 → FailedAndRolledBack
9. 回滚后未恢复 → FailedRollbackIncomplete
10. 回滚后读取不完整 → PositionUncertain
11. Writer 边界不使用 WaitAsync
12. 验证不被 UI 取消跳过

## Writer 安全扫描

- LayoutPreviewWindow: 0 MoveDesktopIcon
- Poc/LayoutTemplates/Preview: 0 MoveDesktopIcon
- Core/Templates/Execution: 0 Writer/Coordinator/SnapshotStore
- 0 SQLite Schema 变更
- 0 真实 Explorer 写入测试

## 本阶段未实现的能力

- 临时中转（staging）的真实执行（M2-04C）
- RecoverySession 持久化（M2-04C）
- 跨重启恢复（M2-04C）
- 人工处理锁持久化（M2-04C）
- 后台监听和无人值守自动执行（M3）

## 后续受控实机验收步骤

M2-04C 完成 staging 和持久化恢复后：
1. 在真实桌面上创建测试图标
2. 创建模板和规则
3. 打开 LayoutPreviewWindow
4. 生成预览，检查候选
5. 执行简单的无中转批量整理
6. 验证图标位置
7. 测试回滚
8. 测试人工处理锁

注意：不要在自动化测试中连接真实 Explorer Writer。
