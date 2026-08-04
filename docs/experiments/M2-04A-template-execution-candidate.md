# M2-04A：模板规则感知执行候选计划

## 阶段目标

新增纯 Core 转换链：
`DesktopLayoutRuleAwarePreviewPlan → TargetSnapshot → RestorePlan → ExecutionPlan → ExecutionCandidate`

这条链只描述"如果执行，会做什么"，不产生任何副作用。

## 为什么复用 M1 Restore/Execution Planner

M1 的 `TargetDisplayLayoutRestorePlanner` 和 `TargetDisplayLayoutExecutionPlanner`
已实现完整的 StableId 匹配、静态占用检测、依赖图分析、Tarjan 环检测、
临时中转规划和执行步骤排序。M2-04A 不重复这些算法。

## Preview → TargetSnapshot 映射表

| PreviewItem | TargetPosition | MonitorDeviceName |
|---|---|---|
| OutOfScope | CurrentPosition | 保持 |
| Excluded | CurrentPosition | 目标显示器 |
| Pinned | CurrentPosition | 目标显示器 |
| ZoneOverride/Default AlreadyAtTarget | TargetPosition (=Current) | 目标显示器 |
| ZoneOverride/Default MoveRequired | TargetPosition | 目标显示器 |
| Unassigned | 拒绝 | — |

## 输入陈旧检查

- FailedItemCount > 0
- IsAutoArrangeEnabled
- IconSpacing 变化
- 图标数量变化
- StableId、DisplayName、ParsingName、ItemKind、PixelPosition、MonitorDeviceName、system/filesystem 标记变化
- 目标显示器缺失/重复/引用变化

## BuildResult / Issue 语义

- `Success`：Candidate 成功生成
- `Rejected`：包含具体 Issues，不创建部分 Candidate
- 15 种 IssueCode

## Candidate 模型

- SourcePreviewPlan、TargetSnapshot、RestorePlan、ExecutionPlan
- IsNoOp：Preview 无 MoveRequired 且 RestorePlan.IsNoOp 且 ExecutionPlan.IsNoOp
- IsExecutable：Preview完整 且 无阻断 且 ExecutionPlan 可执行
- RequiresUserConfirmation：IsExecutable && !IsNoOp

## 确定性

相同输入 → 相同顺序、坐标、分类、Steps、Issues（SnapshotId/时间除外）

## 测试数量

- 新增：21 个（CandidatePlanner 14 + TargetSnapshot 6 + Safety 1）
- 全量：1176/1176 通过

## Writer 零调用扫描

- Execution 目录：0 MoveDesktopIcon / ExecuteWithScopeGateAsync / Coordinator / SnapshotStore

## M2-04B 建议范围

- WPF 执行按钮接入 Candidate
- 二次确认 UI
- 执行结果展示

## 本阶段没有真实桌面移动
