# M2 阶段收尾总结

> 状态：核心实现与单屏实机安全验收完成；真实双显示器拓扑验收待补  
> 收尾日期：2026-08-08  
> 当前最终代码节点：`c3fb1c9d3df46b89ded81288a115a54920234a3b`（PR #49）  
> 正式里程碑状态：仍为“进行中”，待真实双显示器 topology acceptance 通过后改为“已完成”

## 1. 阶段结论

M2 已经完成 DesktopAgent v1.0 手动整理主线的核心闭环：用户可以创建模板与 Zone、配置规则、生成规则感知预览、查看真实移动路径与风险，在显式确认后通过统一安全执行链对 Explorer 原生桌面图标进行批量整理，并在异常、中断或进程崩溃后通过持久化 RecoverySession 与 fresh Shell observation 安全收敛。

截至本次收尾，M2 的核心实现、自动化回归以及单显示器 Windows Explorer 实机安全验收均已通过。当前唯一未完成的阶段级实机项是：在真实双显示器环境中，预览后改变显示器排列、DPI/缩放或断开目标显示器时，验证陈旧候选必须 fail-closed 且 Writer 为 0。

因此本文件采用“核心收口完成、双屏验收待补”的状态，不提前把 M2 标记为正式 Completed，也不让当前缺失的物理双屏环境阻塞 M3 的只读感知与建议模式开发。

## 2. M2 交付能力

### 2.1 模板、Zone 与规则

- 布局模板和 Zone 领域模型；
- SQLite 模板持久化；
- 目标显示器绑定；
- Zone 容量、排序、PlacementFlow 与确定性槽位；
- WPF 模板编辑器；
- Exclude、Pin、ZoneOverride、Default 规则；
- 规则 SQLite 持久化与 WPF 编辑；
- Fixed 图标在 Preview 中保留观察语义，但不进入 execution TargetSnapshot。

### 2.2 Preview 与执行候选

```text
模板 + Zone + 规则
→ Rule-aware PreviewPlan
→ TargetSnapshot
→ RestorePlan
→ ExecutionPlan
→ ExecutionCandidate
```

交付：

- 当前/目标真实比例预览；
- 移动轨迹和规则分类；
- 已就位、需移动、Fixed、Unassigned 等状态；
- no-staging、依赖链、cycle/staging 的统一执行候选；
- staging 风险显式提示；
- Candidate 类型冻结，防止确认后 staging/no-staging 语义漂移；
- stale preview、缺失图标、能力变化和拓扑变化均在执行前 fresh reread / rebuild 阶段重新验证。

### 2.3 批量真实执行

用户可见链路：

```text
选择模板
→ fresh reread
→ 规则感知 Preview
→ ExecutionCandidate
→ 风险说明
→ 显式授权
→ 二次确认
→ fresh reread / rebuild
→ 语义比较
→ Coordinator fresh replan
→ ScopeGate
→ 安全快照
→ Writer
→ 独立验证
→ 终态
```

交付：

- no-staging 真实批量写入；
- DAG 依赖顺序；
- cycle 展开；
- MoveToTemporaryStaging；
- MoveFromTemporaryStagingToTarget；
- Writer 前安全快照保存与回读；
- Attempt 审计；
- 独立重新读取验证；
- Completed、FailedBeforeWrite、FailedAndRolledBack、FailedRollbackIncomplete、PositionUncertain 等终态。

## 3. 持久化恢复模型

M2 将恢复事实来源固定为：

> 真实且稳定的桌面观察负责事实；Journal / Store 负责历史与意图；Recovery 只依据可证明的当前事实做安全决策；Writer 只负责执行命令，无权证明命令最终已经生效。

核心原则：

```text
Observe → Compare → Act
```

不得根据日志顺序猜测真实桌面状态。

对于 staging/cycle，Recovery 使用 ExecutionPlan 的合法执行前缀作为可证明状态集合。例如两图标 swap：

```text
prefix 0: A=P0,      B=P1
prefix 1: A=Staging, B=P1
prefix 2: A=Staging, B=P0
prefix 3: A=P1,      B=P0
```

重启后当前桌面必须唯一匹配某个 legal prefix 才允许继续恢复；不匹配时进入 PositionUncertain 并锁住后续真实写入。

## 4. 单屏 Windows 实机验收

验收环境：Windows 11、Explorer 原生桌面、单显示器 `\\.\DISPLAY1`、工作区约 `2560x1540`、DPI `120x120`、缩放 `125%`、IconSpacing `93x122`。

测试模板：`M2 Acceptance`；测试对象主要为 `M2-A.txt` 与 `M2-B.txt`；回收站和 `icons` 文件夹使用 Exclude / Fixed。

### 4.1 正常执行

| 场景 | 结果 |
|---|---|
| no-staging 单图标真实恢复 | `Completed`，ExactSuccess |
| 两图标 swap / cycle | 3-step staging，`Completed`，3/3 ExactSuccess |
| staging 风险 UX | cycle / 临时中转 / 多次移动 / 中断恢复风险均明确展示 |
| 执行后重新预览 | `AlreadyAtTarget=2`，`MoveRequired=0`，`ExecutionSteps=0` |
| 重启 | 无 unresolved recovery |

### 4.2 Forward physical effect before checkpoint

通过 Debug-only 受控故障注入，在第一个 staging Writer 已精确完成物理写入、调用方尚未持久化 checkpoint 时 `FailFast`。

结果：

```text
真实桌面先到 legal prefix 1
→ 进程终止
→ 重启发现 unresolved / Executing
→ fresh observation 精确识别 legal prefix
→ Recovery rollback
→ Original
→ unresolved 清空
```

验收通过。

### 4.3 Rollback physical effect before checkpoint

在 Recovery 第一次真实 rollback Writer 已精确恢复物理位置、rollback checkpoint 尚未持久化时再次 `FailFast`。

结果：

```text
真实桌面已经回到 Original
→ 进程终止
→ 再次重启仍加载 Executing session
→ fresh observation 发现当前已经是 legal prefix 0 / Original
→ 本次 Rollback attempts = 0
→ 不重复 Writer
→ 验证 Passed
→ unresolved 清空
```

验收通过。

这证明前向和回滚两个最关键的“物理世界比持久化状态快一步”的 crash window 均可依靠 observation-first recovery 正确收敛。

### 4.4 stale Preview

生成可执行 Preview 后手动改变测试图标位置，再尝试执行旧候选。

结果：

- fresh reread 发现当前位置与 Preview 不同；
- UI 明确提示桌面状态在预览后发生变化；
- Writer 未启动；
- 要求重新生成 Preview 并重新确认。

验收通过。

### 4.5 Preview 后图标缺失

生成可执行 Preview 后，将 `M2-A.txt` 临时移出桌面，再尝试执行旧候选。

结果：

- `InputIconCount: 4 -> 3`；
- TargetSnapshot / RestorePlan / ExecutionPlan 同步失效；
- `ExecutionPlan.Steps.Count: 1 -> 0`；
- Writer 未启动；
- 文件恢复后重新 Preview 可正常执行并 `Completed`。

验收通过。

### 4.6 Auto Arrange

开启 Explorer “自动排列图标”后生成候选。

结果：

```text
[AutoArrangeEnabled] 自动排列图标已开启
BuildResult.Outcome = Rejected
```

执行候选不成立，Writer 无入口。Writer 后端自身还保留连接后和移动前两次 Auto Arrange 检查。

验收通过。

## 5. 实机验收中发现并修复的生产缺陷

### 5.1 多步骤异常终态丢失 completed forward prefix

多步骤 Forward 已有 ExactSuccess 后，如果后续发生 WriterException、StableIdMismatch 或 PreviousPositionMismatch，旧实现会在终态持久化时错误写入空 `CompletedForwardStepIndexes`，与 ForwardAttempts 自相矛盾，最终被误报为 `TerminalPersistFailed`。

修复：completed indexes 从真实 ExactSuccess attempts 投影，不放宽 RecoverySession validator。

### 5.2 Fixed Preview 语义裂缝

Exclude / Pin 会得到 Fixed placement；旧 SceneBuilder 将 Fixed 错映射为 AlreadyAtTarget，导致缺少 Target Canvas 时预览异常。

修复：补充真正的 Fixed visual state；Fixed / Unassigned 不要求目标坐标。

### 5.3 Fixed 错误进入 execution TargetSnapshot

Fixed 项被旧逻辑作为“当前位置目标”放入 execution TargetSnapshot；真实回收站属于 SystemItem，因此 Restore Planner 会正确拒绝“恢复系统项目”，导致普通文件移动也被整体 Blocked。

修复：Fixed 继续留在 current observation 中作为真实占位者，但不进入 Writer 的执行目标集合。

### 5.4 staging 网格相位错误

旧 staging 扫描从 `WorkArea.Left/Top` 作为网格原点。实机 IconSpacing 为 `93x122`，但 Explorer 稳定网格实际为：

```text
x = 16 + n*93
y = 2 + m*122
```

因此理论 staging `(186,0)` 会在 Explorer 稳定后变成 `(202,2)`，导致重启 Recovery 无法精确匹配 legal prefix，并正确 fail-closed 为 PositionUncertain。

修复：从已被 Shell 稳定观察到的当前图标位置推导 spacing 相位，再扫描 staging 候选。没有给 Recovery 增加坐标容差，也没有放宽 legal-prefix 精确证明。

## 6. 当前 CI 与代码状态

当前收尾代码节点：

```text
c3fb1c9d3df46b89ded81288a115a54920234a3b
```

对应 Draft PR #49：`fix: 让 staging 遵循 Explorer 实际网格相位`。

- GitHub Actions #173：success；
- 本地 Debug Build：通过；
- 本地全量测试：通过；
- PR #49：Draft、未合并；
- 当前 stacked PR 均未因本次收尾自动合并。

## 7. 唯一阶段级 Pending：真实双显示器 topology acceptance

当前旅行环境没有可用真实双显示器，因此以下实机项未冒充通过：

```text
生成可执行 Preview
→ 改变真实双屏排列 / 工作区 / DPI / 缩放，或断开目标副屏
→ 尝试执行旧候选
→ fresh reread / topology comparison fail-closed
→ Writer = 0
→ 必须重新 Preview
```

代码和自动测试已覆盖拓扑 / 目标显示器 /能力变化相关安全条件，但正式 M2 Completed 仍要求在真实双显示器 Explorer 环境补一次上述验收。

补验通过后：

1. 将 roadmap 中 M2 状态改为“已完成”；
2. 将本文件状态改为正式完成；
3. 解除 M3 对 unattended real-write 的临时限制。

## 8. M3 临时安全门

在真实双屏 topology acceptance 补完前，允许提前实施 M3 的只读与建议主线：

- 托盘常驻基础；
- 文件系统 / Windows 事件触发提示；
- debounce；
- stable observation；
- Shell Reader 完整重读；
- state diff；
- 自动选择模板 / 规则的只读逻辑；
- Suggestion 模式；
- 通知；
- 自动化历史与诊断基础。

暂时禁止：

- 无人值守真实 Writer；
- topology-sensitive 自动执行；
- 绕过 M2 ScopeGate / Coordinator / Recovery 的任何写路径；
- 在真实双屏验收前将 M2 标记为正式 Completed。

这允许 M3 从“眼睛和耳朵”开始实施，同时继续保持 v1.0 冻结安全链路。

## 9. 收尾结论

M2 已经从“模板与规则的纯规划能力”推进为可在真实 Explorer 上完成 no-staging、DAG、cycle/staging、持久化恢复和 crash reconciliation 的完整手动整理内核。

当前剩余工作不再是 M2 核心算法或恢复架构缺口，而是一个受物理环境限制的真实双显示器 topology acceptance。

在补验之前，M3 可以开始只读感知与建议模式开发；无人值守真实写入继续保持关闭。
