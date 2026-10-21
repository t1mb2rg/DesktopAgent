# ADR-0004：AI 获得完整逻辑工作区规划权，但不获得 Writer Authority

- 状态：已取代（Superseded）
- 日期：2026-08-19
- 被取代于：2026-08-27
- 取代它的决策：ADR-0005《AI Workspace Blueprint 与 Explorer 原生编译》

> 历史说明：本文保留 ADR-0004 当时的完整决策与背景。M5-07.7 后续真实 Windows Physical Gate 证明“AI 直接规划完整逻辑网格与逐项 exact cell”会把 Explorer 几何权威、AI 设计权和本地施工权混在一起，因此该目标架构已由 ADR-0005 取代。ADR-0004 中关于 Writer 隔离、Frozen Preview、fresh observation、Workspace 事务语义和 fail-closed Recovery 的原则仍被后续架构继承。

## 背景

M5-07.7 的真实双显示器 Physical Gate 暴露出当前 AI 布局链路的结构性问题。

当前实现把职责分成：

```text
AI 生成语义分类、粗粒度 Zone 提示和显示器偏好
    ↓
本地 MultiDisplayWorkspaceCompiler 推断目标屏、补齐 Zone 并生成几何
    ↓
Preview / Candidate / Execution Planner
    ↓
Writer
```

这条链路虽然隔离了 Writer，但 AI 对最终物理布局的表达能力过弱，而本地转换器承担了过多意图解释权。真实验收中已经出现：AI 明确要求所有可移动项目进入副屏，主屏最终 Assignment 为 0；转换器却为了让参与显示器拥有“有效几何”，把副屏语义 Zone 复制进主屏 Draft。结果既不忠实于 AI 意图，也让 Preview 难以解释。

问题不在 Writer 安全边界，而在 AI 与 Preview 之间：

- AI 只能输出 `placement_hint` / `size_hint` 等模糊语义；
- Zone 没有明确的 `display_ref`；
- AI 不能表达每个项目的最终格位；
- 本地转换器会静默补齐、复制或推断布局事实；
- Observed Workspace 与 Desired Workspace 混在同一份 Draft 表达中。

## 决策

DesktopAgent 将开放 AI 对完整**逻辑工作区终态**的规划权，但继续禁止 AI 获得任何 Writer authority。

长期目标链路为：

```text
Fresh Observed Workspace
    ↓
AI Desired Workspace Plan（逻辑网格终态）
    ↓
Local Validator（只接受或拒绝）
    ↓
Frozen Preview
    ↓
User Confirm
    ↓
Fresh Observation + Semantic Equality
    ↓
Deterministic Execution Planner
    ↓
Safety Snapshot / Recovery Checkpoint
    ↓
Primitive Writer
    ↓
Post-write Observation / Commit or Rollback
```

核心原则是：

> 开放 AI 的感知与终态规划能力，继续锁住 AI 的执行能力；本地代码负责验图和施工，不再替 AI 脑补用户意图。

## Authority 划分

### AI Planner

AI 可以读取经过本地投影的逻辑工作区：

- 语义显示器引用、主副屏角色和相对拓扑；
- 每块显示器的可用逻辑网格行列数；
- 图标网格间距和网格相位的逻辑表示；
- 每个临时 `item_ref` 当前所在显示器与逻辑格位；
- 项目名称、类型、语义分类和可移动性；
- 固定系统项目及其占用格；
- 当前 Zone、保留区域和用户锁定约束；
- 用户自然语言布局要求。

AI 可以输出完整 Desired Workspace Plan：

- 每个 Zone 所属的 `display_ref`；
- 每个 Zone 的逻辑网格边界；
- 每个保留区域的逻辑网格边界；
- 每个项目的最终显示器与目标格；
- `Place`、`Keep` 或 `Exclude` 的显式项目状态；
- 跨屏迁移关系与面向用户的规划理由。

AI 不得：

- 获得或调用 `IDesktopIconPositionWriter`；
- 输出 Writer command、Shell command、脚本或文件操作；
- 输出 Windows 设备名、PIDL、StableId 或真实文件路径；
- 输出 Windows 虚拟桌面物理像素坐标；
- 决定实际移动顺序、临时中转、回滚或恢复行为；
- 删除、复制、重命名、归档或修改文件内容。

### Local Validator

Validator 对 AI Plan 进行确定性校验，包括但不限于：

- 每个已观察项目恰好得到一种明确状态；
- 不存在未知、重复或伪造的 `item_ref`；
- 显示器、Zone 和项目格位引用合法；
- 项目和 Zone 不越界；
- 项目格位不碰撞；
- Zone 之间不非法重叠；
- Zone 与保留区域不重叠；
- Zone 容量足够并包含其项目；
- 固定项目保持原屏和原格；
- 固定障碍物的占用格不被分配；
- Plan 绑定的 Observation、拓扑和网格事实仍然有效。

默认情况下，Validator **只能接受或拒绝**，不得静默移动项目、复制 Zone、改变显示器目标或生成 AI 未输出的布局事实。

校验失败时，产品可以把结构化错误返回 AI，允许有限次数重新规划。重试期间仍然没有 Preview、Candidate 或 Writer authority。

若未来提供“本地自动修复小碰撞”能力，它必须是用户显式启用的独立模式，并在 Preview 中展示原始 Plan 与修复后 Plan 的完整差异；不得成为默认行为。

### Execution Planner

Execution Planner 只处理如何从已观察初态安全到达已验证终态：

- 把显示器本地逻辑格转换为虚拟桌面物理像素；
- 计算确定性的项目移动顺序；
- 识别占位依赖和循环；
- 选择安全的临时中转格；
- 生成执行步骤和反向恢复步骤。

AI 决定“最终桌面长什么样”，Execution Planner 决定“如何安全到达那里”。

### Writer

Writer 继续维持最小原语：

```text
MoveDesktopIcon(stableId, targetPixelPosition)
```

Writer 不解释意图、不规划布局、不选择显示器、不处理文件系统操作，也不自行决定移动顺序。

### Coordinator

现有执行安全链继续保留：

- 用户必须先看到 frozen fresh Preview 并显式确认；
- Apply 前重新读取真实桌面；
- Plan 必须与 fresh Observation 保持执行语义一致；
- StableId 必须唯一匹配；
- 显示器拓扑、工作区或网格变化使旧 Plan 失效；
- Auto Arrange 开启时拒绝写入；
- Writer 前创建并回读 safety snapshot 与 recovery checkpoint；
- Writer 后重新观察真实桌面并独立验证；
- 执行失败时回滚；
- 位置不确定时停止继续写入并进入人工恢复锁。

## 数据模型

### Observed Workspace

Observed Workspace 只表达当前真实事实：

- Observation identity 与捕获时间；
- 显示器拓扑、工作区和网格信息；
- 每个项目的当前显示器、格位和可移动性；
- 固定障碍物；
- 当前 Zone 和当前保留约束。

Observed Workspace 不携带执行 authority。

### Desired Workspace Plan

Desired Workspace Plan 表达 AI 规划的完整最终状态：

- 所有参与显示器；
- 所有目标 Zone 及其 `display_ref` 和网格边界；
- 所有保留区域；
- 所有项目的显式最终状态；
- 跨屏迁移关系；
- 所依赖的 Observation、拓扑和网格指纹；
- Plan schema、Provider 和模型版本等诊断元数据。

每个项目必须明确为：

```text
Place   放到指定显示器和逻辑格
Keep    保持当前显示器和逻辑格
Exclude 不进入本次执行范围
```

字段缺失不得被解释为“沿用本地默认值”。

Observed Workspace 与 Desired Workspace Plan 必须是两个独立对象。源显示器 0 个目标项目时，目标状态可以为空；不得为满足 Draft 结构而复制其他显示器的 Zone。

## 坐标模型

AI 使用显示器本地逻辑网格，不直接处理 Windows 物理像素。

示例项目终态：

```json
{
  "item_ref": "item-0042",
  "state": "place",
  "target_display_ref": "secondary_1",
  "target_cell": { "column": 3, "row": 5 }
}
```

示例 Zone：

```json
{
  "zone_key": "work",
  "display_ref": "secondary_1",
  "column": 0,
  "row": 0,
  "width": 8,
  "height": 5
}
```

DPI、任务栏、负坐标显示器、工作区偏移、网格间距和真实像素换算继续由本地确定性代码处理。

## 多显示器与事务边界

Zone 必须显式绑定 `display_ref`，不再由 Category/Assignment 目标反推所属显示器。

源侧与目标侧必须分别表达：

- 源侧展示真实源状态、迁出项目、固定障碍物和源侧保留约束；
- 目标侧展示 AI 规划的 Zone 与目标项目；
- 0 个目标项目的显示器不复制其他显示器的 Zone。

一份 Plan 如果影响多块显示器，则应被视为一个 Workspace 级事务。Writer 仍可逐项执行，但 Preview、用户确认、快照、恢复和最终验证必须覆盖全部受影响项目与显示器，避免留下半完成跨屏状态。

## Preview 与用户编辑

Preview 必须如实渲染已验证并冻结的 Desired Workspace Plan，不得再次调用启发式布局算法改变终态。

产品应支持：

- 当前状态与目标状态对比；
- 跨屏迁移数量与方向汇总；
- 按来源屏、目标屏、类别或 Zone 过滤迁移；
- 单项起点与终点查看；
- 固定项、排除项、冲突和容量诊断；
- 用户锁定图标、类别、Zone、显示器或保留区域。

用户编辑后的 Plan 必须重新验证并重新 Preview。用户锁定项作为后续 AI 规划的 hard constraint。

默认视图不应同时绘制全部迁移线；大量迁移应优先显示汇总流，选中或过滤后再显示具体连线。

## 隐私边界

逻辑网格位置本身可以进入 AI 上下文，但产品应允许用户检查本次将发送的数据，并支持：

- 隐藏指定项目名称；
- 只发送类型和扩展名；
- 排除敏感项目；
- 关闭正文分析；
- 使用临时、请求内有效的 `item_ref`；
- 选择本地或远程 Provider。

正文分析继续遵循独立显式 gate、类型白名单、长度上限和不发送真实路径的现有边界。

## 与既有决策的关系

- 本 ADR 延续 ADR-0003 的 Observation / Writer 隔离原则。AI 与只读规划路径不得获得 Writer 依赖。
- ADR-0002 的任意显示器拓扑与 Windows 虚拟桌面物理坐标事实继续有效；只是 AI 通过显示器本地逻辑网格观察和规划，本地层负责物理换算。
- M5-07.6 中“AI 不生成物理坐标”的边界继续有效。这里新增的是逻辑网格终态，不是 Windows 物理像素，也不构成执行 authority。
- 当前 `DesktopUnderstandingMultiDisplayWorkspaceCompiler` 是过渡实现，不应被视为本 ADR 的目标架构。后续改造应逐步移除其静默几何补齐与语义脑补职责，而不是继续扩大这些职责。

## 后果

优点：

- Preview 忠实反映 AI 已经规划好的最终桌面；
- 消除本地转换器静默复制 Zone、猜测目标屏和补造几何的问题；
- AI 可以利用真实占位、网格容量和固定障碍物完成更可靠的整体规划；
- Writer 与恢复链继续保持最小权限和 fail-closed；
- AI Plan、Validator、Execution Plan 和 Writer authority 的责任边界可以独立测试。

代价：

- AI 请求与响应体会显著增大；
- 需要新的 Workspace Plan schema、Validator 和 Preview 投影；
- 大型桌面可能需要分阶段规划，但最终仍必须合并并验证为一份完整 Plan；
- AI 数值规划可能产生碰撞、越界或遗漏，因此必须有结构化校验与有限重试；
- 多显示器 Apply 需要明确 Workspace 级事务和恢复语义。

## 非目标

本 ADR 不授权：

- AI 直接或间接调用 Writer；
- AI 执行任何文件系统修改；
- 绕过 Preview、用户确认、fresh observation 或 recovery gate；
- 让 Validator 默认静默修改 AI Plan；
- 以 AI confidence 或自然语言声明替代本地执行校验。

本 ADR 只冻结后续改造方向；具体 schema 版本、重试次数、Workspace 事务实现和迁移步骤由后续实验文档与代码变更确定。
