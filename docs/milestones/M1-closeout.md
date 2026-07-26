# M1 阶段收尾总结

> 状态：已完成  
> 完成日期：2026-07-26  
> 最终主线提交：`1ce143dd260c025113e291defac11dacdc0a8302`（PR #9）

## 1. 阶段结论

M1 已完成 DesktopAgent 在 Windows 11 原生 Explorer 桌面上的核心技术验证：

- 读取任意数量显示器及虚拟桌面物理像素坐标；
- 只读枚举桌面图标、稳定身份、类型、位置和显示器归属；
- 保存并持久化完整桌面布局快照；
- 通过公开 Shell COM 接口定位普通文件系统图标；
- 在任何真实写入前重新读取、重新规划并保存安全快照；
- 生成指定显示器布局恢复计划和确定性原子执行步骤；
- 对依赖、静态阻断、循环和临时中转进行纯逻辑分析；
- 通过安全执行协调器完成逐步验证、审计和保守回滚；
- 在 WPF PoC 中完成单图标、单步骤、无中转的受控真实恢复；
- 通过独立重新读取确认最终桌面位置。

M1 的最终产物不是正式桌面整理产品，而是一套可复用、可测试、可审计的桌面布局安全底座。

## 2. 范围演进

项目早期 README 将“测试分区”和“分区层不影响原生桌面交互”列入 M1 验收目标。实际推进中，M1 的范围被收敛为更基础、风险更高的 Shell 读写与恢复闭环：

```text
显示器拓扑
→ 桌面图标读取
→ 布局快照
→ 安全移动后端
→ 恢复计划
→ 执行步骤计划
→ 安全执行协调器
→ 受控真实恢复
→ 独立读回验证
```

以下能力没有在 M1 中实现，明确顺延到后续阶段：

- 可视化桌面分区层；
- 分区模板和用户规则；
- AI 文件用途识别与分类建议；
- 自动整理、后台监听和计划任务；
- 文件系统归档或目录迁移；
- 正式产品 UI、托盘程序和安装包。

因此，M1 的“完成”是指安全技术底座完成，不代表完整产品功能完成。

## 3. 阶段交付

### 3.1 M1-01：显示器拓扑与目标显示器

交付：

- `EnumDisplayMonitors`、`GetMonitorInfo` 和 DPI 读取；
- 虚拟桌面物理像素坐标、工作区、主屏和缩放；
- 支持负 X / 负 Y 坐标；
- `TargetDisplaySelector` 保持目标并在设备消失时安全回退；
- PerMonitorV2 DPI Awareness。

实验记录：[`M1-01-display-topology.md`](../experiments/M1-01-display-topology.md)

### 3.2 M1-02：桌面图标只读枚举

交付：

- `FindWindowSW + SWC_DESKTOP` 主连接路径；
- Explorer 枚举降级路径；
- `IShellBrowser`、`IShellView`、`IFolderView2` 只读调用链；
- 图标名称、ParsingName、StableId、类型和位置；
- 完整 COM、PIDL 和 STRRET 生命周期管理；
- 读取失败计数、连接方式和主路径异常诊断；
- 显示器归属分配。

实机读取曾验证 116 个桌面图标、0 个失败，连接方式为 `FindWindowSW`。

实验记录：[`M1-02-desktop-icon-reading.md`](../experiments/M1-02-desktop-icon-reading.md)

### 3.3 M1-03A / M1-03B：布局快照与 SQLite

交付：

- 不可变 `DesktopLayoutSnapshot` 和图标快照项；
- `DesktopLayoutSnapshotFactory`；
- `IDesktopLayoutSnapshotStore`；
- SQLite 初始化、保存、读取、列表和删除；
- 父记录与图标项事务原子性；
- UTC、枚举、布尔值和负坐标完整往返；
- PoC 手动保存完整快照和查看最近快照；
- 应用重启后快照持久化；
- 读取不完整时禁止保存。

默认数据库：

```text
%LocalAppData%\DesktopAgent\desktop-agent.db
```

实验记录：

- [`M1-03A-layout-snapshot-persistence.md`](../experiments/M1-03A-layout-snapshot-persistence.md)
- [`M1-03B-poc-snapshot-ui.md`](../experiments/M1-03B-poc-snapshot-ui.md)

### 3.4 M1-04A：单图标写入后端

交付：

- `IDesktopIconPositionWriter`；
- 基于 `IFolderView.SelectAndPositionItems` 的 Shell COM Writer；
- 自动排列状态和图标间距读取；
- Writer 内重新枚举并以 Ordinal StableId 唯一匹配当前 PIDL；
- 自动排列写入前双重检查；
- Right → Left → Down → Up 相邻空位规划；
- AABB 半开矩形碰撞检测；
- 写入后实际位置读回和 `IsExact`。

实验记录：[`M1-04A-single-icon-positioning-backend.md`](../experiments/M1-04A-single-icon-positioning-backend.md)

### 3.5 M1-04B：单图标移动与同会话恢复

交付：

- WPF 显式选择和二次确认；
- 真实写入前完整重读；
- 安全快照 Save → Get 回读验证；
- Writer 前创建 `MoveRecoverySession`；
- Writer 异常后保留恢复入口并标记位置不确定；
- 只有精确回到原位置才清除恢复会话；
- 活跃恢复会话和窗口关闭保护。

受控实机验证：

| 字段 | 结果 |
|---|---|
| 测试对象 | `DesktopAgent-M1-04B-Test` |
| SafetySnapshotId | `6bd3daea-7466-4d4f-80d7-527c0dcbec91` |
| 原位置 | `(2114,507)` |
| 请求位置 | `(2190,507)` |
| 移动实际位置 | `(2190,507)` |
| 恢复实际位置 | `(2114,507)` |
| 移动阶段 | `IsExact = true` |
| Writer 异常 | 无 |
| 位置不确定 | 否 |

实验记录：[`M1-04B-poc-single-icon-move-restore.md`](../experiments/M1-04B-poc-single-icon-move-restore.md)

### 3.6 M1-05A：指定显示器恢复计划

交付：

- 一个快照目标对应一个计划项；
- 快照 StableId 重复、目标坐标重复和当前桌面全局重复检测；
- 缺失、系统项、非文件系统项、无显示器归属和目标越界分类；
- 全量目标占用者扫描；
- 静态占用者和阻断固定点传播；
- MoveRequired 依赖关系；
- Tarjan SCC 环检测；
- 确定性拓扑执行顺序；
- `RequiresTemporaryStaging` 和阻断诊断。

本阶段只生成计划，不调用 Writer。

实验记录：[`M1-05A-target-display-layout-restore-planner.md`](../experiments/M1-05A-target-display-layout-restore-planner.md)

### 3.7 M1-05B：原子执行步骤计划

交付：

- `MoveToTarget`；
- `MoveToTemporaryStaging`；
- `MoveFromTemporaryStagingToTarget`；
- 当前桌面位置与显示器状态的内存模拟；
- 安全中转位置扫描、动态释放和复用；
- 无环路径严格遵循 `MoveExecutionOrder`；
- 有环路径通过 staging 展开；
- 源计划摘要、Dependencies、Cycles 和陈旧状态重新验证；
- 失败原子性：失败计划不公开部分 Steps 或 StagingAssignments。

本阶段仍为纯逻辑，不调用 Writer。

实验记录：[`M1-05B-target-display-layout-execution-step-planner.md`](../experiments/M1-05B-target-display-layout-execution-step-planner.md)

### 3.8 M1-05C-A：安全执行协调器

交付：

- 最新 capabilities、显示器拓扑和完整桌面读取；
- 显示器归属重新分配；
- 不接受陈旧 ExecutionPlan，执行前重新规划；
- 安全快照创建、保存、回读和逐字段验证；
- Writer 前创建并深度冻结 RecoverySession；
- 正向和回滚 Attempt 的结构化审计；
- Writer 中不使用 `WaitAsync(cancellationToken)`；
- Writer 异常、StableId mismatch 和 PreviousPosition mismatch 进入 `PositionUncertain`；
- 仅 `InexactKnown` 允许保守自动回滚；
- `FailedBeforeWrite`、`FailedAndRolledBack`、`FailedRollbackIncomplete` 和 `PositionUncertain` 明确区分。

本阶段通过 Fake Reader、InMemory Store 和 Scripted Writer 验证，没有连接 Explorer。

实验记录：[`M1-05C-A-target-display-layout-execution-coordinator.md`](../experiments/M1-05C-A-target-display-layout-execution-coordinator.md)

### 3.9 M1-05C-B：受控真实恢复

交付：

- WPF 独立布局恢复入口；
- 预览、点击执行复检和 Coordinator 最新计划三层 ScopeGate；
- 真实写入限定为单图标、单步骤、无中转；
- 二次用户确认；
- 完整执行结果和 Attempt 诊断；
- `FailedRollbackIncomplete` / `PositionUncertain` 人工处理锁；
- 人工处理锁同时禁止 M1-04B 新写入；
- 修复边缘 `AlreadyAtTarget` 图标错误阻断问题。

最终受控实机验收：

| 字段 | 结果 |
|---|---|
| 目标显示器 | `\\.\DISPLAY2` |
| 测试对象 | `DesktopAgent-M1-05C-B-Test` |
| 基准快照 | `7e7a4f6d-4444-48af-b451-5e98f903f0fb` |
| 基准位置 | `(1886,507)` |
| 手工移动后位置 | `(1962,507)` |
| MoveRequiredCount | 1 |
| BlockedCount | 0 |
| ExecutionPlan.Steps.Count | 1 |
| UsesTemporaryStaging | false |
| Gate.IsAllowed | true |
| 执行状态 | `Completed` |
| Attempt 结果 | `ExactSuccess` |
| SafetySnapshotId | `8b6ab498-4b98-494d-b788-ab7b85ee0840` |
| SessionId | `b6362dcc-9d6a-4529-b42e-cbb04413ec8d` |
| 请求 / 实际位置 | `(1886,507)` / `(1886,507)` |
| Forward / Rollback Attempts | 1 / 0 |
| RequiresManualRecovery | false |
| IsPositionUncertain | false |
| 独立重新读取 | 118 个图标、0 失败、最终位置 `(1886,507)` |

实验记录：[`M1-05C-B-controlled-target-display-restore-poc.md`](../experiments/M1-05C-B-controlled-target-display-restore-poc.md)

## 4. 最终架构

```text
DesktopAgent.Core
├─ Displays / Geometry
├─ DesktopIcons
├─ DesktopLayouts / Snapshot
├─ DesktopLayouts / Restore Planner
├─ DesktopLayouts / Dependency Graph
├─ DesktopLayouts / Execution Planner
└─ Execution Result / Recovery Session models

DesktopAgent.WindowsInterop
├─ Win32 display topology
├─ Shell desktop reader
├─ Shell icon position writer
├─ Safety snapshot factory
└─ TargetDisplayLayoutExecutionCoordinator

DesktopAgent.Persistence
└─ SQLite layout snapshot store

DesktopAgent.Poc
└─ WPF controlled read / snapshot / move / restore UI

DesktopAgent.Tests
└─ Core logic, persistence, planner, coordinator and safety regression tests
```

关键分层约束：

- Core 不依赖 WPF、Shell COM 或 SQLite；
- WindowsInterop 集中封装 Win32 / Shell COM；
- Persistence 只实现存储接口；
- PoC 负责显式用户交互，不承载核心规划算法；
- 真实写入只通过正式 Writer 和 Coordinator 路径发生。

## 5. M1 固化的安全不变量

1. 桌面图标读取必须完整；`FailedItemCount > 0` 时不得保存或执行恢复。
2. StableId 和 DeviceName 使用 `StringComparison.Ordinal` 精确匹配。
3. 自动排列开启时拒绝写入，并在 Writer 内缩小竞态窗口再次检查。
4. 不缓存旧 PIDL；每次 Writer 调用重新枚举当前桌面并唯一匹配。
5. 任何批量操作必须先生成完整、可审计的恢复计划和原子步骤计划。
6. 真实执行前必须重新读取并重新规划，不能直接执行陈旧计划。
7. Writer 前必须保存并回读验证完整安全快照。
8. RecoverySession 必须在第一次 Writer 之前创建。
9. Writer 抛异常不等价于没有副作用，必须视为位置可能不确定。
10. 身份或 PreviousPosition 不可信时禁止盲目自动回滚。
11. 只有 ActualPosition 已知且身份、前置位置可信的非精确结果才允许保守回滚。
12. 安全快照不自动删除；人工恢复证据必须保留。
13. 用户未明确确认时不得执行真实写入。
14. 宁可停止，也不在状态不明确时继续。

## 6. 验证基线

M1 最终验证基线：

- Release Build：0 Warnings，0 Errors；
- Tests：325/325 passed；
- NuGet 漏洞检查：5/5 项目未发现已知易受攻击的软件包；
- GitHub Actions `build #38`：Restore、Build、Test 全部成功；
- 两次受控实机验证均完成精确位置读回；
- M1-05C-B 额外完成独立重新读取确认。

合并的 M1 PR：

| PR | 阶段 | 结果 |
|---|---|---|
| #1 | M1-02 桌面图标只读读取 | merged |
| #2 | M1-03A 快照与 SQLite | merged |
| #3 | M1-03B PoC 快照 UI | merged |
| #4 | M1-04A 单图标 Writer 后端 | merged |
| #5 | M1-04B 单图标移动与恢复 PoC | merged |
| #6 | M1-05A 恢复规划器 | merged |
| #7 | M1-05B 执行步骤规划器 | merged |
| #8 | M1-05C-A 安全执行协调器 | merged |
| #9 | M1-05C-B 受控真实恢复 PoC | merged |

## 7. 已知限制与未验证范围

M1 的结论不覆盖：

- 多步骤或临时中转计划的真实桌面执行；
- 执行失败后的真实桌面自动回滚；
- 程序重启后的 RecoverySession 恢复；
- 跨会话、跨机器或跨 Explorer 版本恢复；
- 显示器热插拔期间的执行；
- DeviceName 在驱动更新或输出切换后的长期稳定性；
- 自动排列开启时的真实拒绝路径；
- 所有 Windows 11 构建和 Explorer 版本；
- 高 DPI 极端混合缩放场景；
- 正式后台常驻、性能和内存长期运行指标；
- 文件系统移动、归档和撤销；
- AI 分类、自动规则和可视分区层。

## 8. M2 交接建议

M2 应从“确定性的桌面分区与布局规则”开始，而不是直接接入 AI 或后台自动整理。

建议顺序：

### M2-01：分区与模板领域模型

- 目标显示器上的矩形分区；
- 分区名称、用途、优先级和容量约束；
- 模板版本和显示器绑定；
- 纯 Core，不接触 WPF 或 Writer。

### M2-02：确定性布局分配规划器

- 将图标集合映射到分区；
- 在分区内生成无重叠网格目标；
- 输出原因、冲突和未分配项；
- 不直接执行，只生成可审计计划。

### M2-03：预览、差异和用户规则

- 显示当前位置、建议位置和移动原因；
- 用户确认、排除、固定和覆盖规则；
- 规则优先于 AI 建议；
- 所有用户修改重新生成计划。

### M2-04：受控多图标执行

- 复用 M1 RestorePlanner、ExecutionPlanner 和 Coordinator；
- 先在自动化测试覆盖多步骤、循环和 staging；
- 再逐级扩大实机 ScopeGate；
- 保留安全快照、审计、人工处理锁和独立读回。

M2 初期明确不做：

- 自动后台执行；
- 无确认写入；
- 文件归档；
- AI 直接产生 Writer 调用；
- 跨重启自动恢复。

## 9. M2 开始前的固定入口

M2 必须复用以下接口和语义，不绕过 M1 底座：

- `IDisplayTopologyReader`
- `IDesktopIconReader`
- `IDesktopIconPositionWriter`
- `IDesktopLayoutSnapshotStore`
- `TargetDisplayLayoutRestorePlanner`
- `TargetDisplayLayoutExecutionPlanner`
- `TargetDisplayLayoutExecutionCoordinator`

任何新的布局策略只能生成计划，不能直接调用 Shell Writer。

## 10. 最终结论

M1 已证明 DesktopAgent 可以在不接管 Explorer 原生桌面的前提下，可靠读取桌面状态、保存完整布局、安全规划恢复步骤，并在严格闸门下完成一次真实、精确、可审计的指定显示器布局恢复。

项目现在具备进入 M2 的条件：下一阶段可以专注于“桌面应当如何分区和布局”，而不必重新解决 Windows Shell 读写、安全快照、执行审计和恢复语义。
