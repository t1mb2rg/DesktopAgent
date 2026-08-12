# Tessel 产品路线图

> 文档状态：活路线图  
> 目标版本：v1.0  
> 首次冻结：2026-08-04  
> 最近修订：2026-08-12  
> 当前阶段：M5-07 Performance / Long-run Stability

## 1. 这份路线图负责什么

本文档维护 Tessel 的**产品边界、技术主线、安全不变量、当前里程碑状态和发布路径**。

Tessel 是对外产品名。仓库、程序集、namespace 和部分源码路径目前继续使用 `DesktopAgent.*` 作为内部工程名，不为了品牌迁移强行重命名稳定代码。

历史实现过程、单次实验、PR 细节和物理验收记录放在 `docs/experiments/` 与 `docs/milestones/` 中，不再让活路线图承担完整施工日志。

长期规则：

- 产品定位、真实文件边界、安全不变量和阶段验收目标需要人工确认；
- 类名、文件路径、具体 Windows API、模型提供方和安装工具可以在不改变外部语义的前提下调整；
- UI、AI、自动化与 Writer 的关系不得通过重构偷偷改变；
- 每个里程碑完成后更新当前状态；
- 路线发生实质变化时更新本文档并记录原因。

## 2. v1.0 产品定位

Tessel v1.0 是一个面向 Windows 11 的本地桌面图标布局与自动整理工具。

它直接理解 Explorer 原生桌面的显示器、图标和空间布局，不创建替代桌面，也不接管 Explorer 的正常交互。

### v1.0 可以做什么

- 读取任意数量显示器的拓扑、工作区、DPI 与桌面图标物理坐标；
- 创建布局模板、可视分区和图标规则；
- 使用用户规则与本地确定性规则生成布局方案；
- 使用可选 AI 提供分类、规则和自然语言建议；
- 基于新的桌面观察结果生成可视预览和执行候选；
- 在用户授权与安全 Gate 允许时进入受控桌面图标执行链；
- 保存安全快照、验证执行结果并在异常时保守回滚或进入恢复流程；
- 常驻 Windows 托盘，根据用户确认过的长期策略决定哪些事情以后可以自动做；
- 在产品化阶段补齐直接桌面分区编辑，使用户即使不启用自动整理也可以先划分空间，再手动把图标拖入对应区域。

### v1.0 明确不做什么

- 不移动真实文件到其他目录；
- 不删除、重命名或归档真实文件；
- 不扫描和管理整个磁盘；
- 不让 AI 直接调用 Shell Writer；
- 不把旧坐标或 WriterCommand 当作可重放任务；
- 不用自绘桌面替换 Explorer 原生桌面。

真实文件分类、移动、归档与文件系统事务恢复属于 v2.0 探索方向，使用独立风险模型。

## 3. 当前产品形态

Tessel 已经明确拆成两个 Surface。

### 3.1 Tessel 正式产品

当前工程入口仍为 `DesktopAgent.WinUI`，面向日常用户：

```text
Tessel
├─ 概览
├─ AI 助手
├─ 待确认
├─ 布局
└─ 设置
```

职责：

- 单实例；
- 常驻托盘，关闭窗口后继续后台运行；
- 产品设置、布局管理、影响预览和桌面分区预览；
- 待确认工作流；
- 自动化方式和 AI 服务配置；
- 只展示用户需要理解的产品状态。

当前正式 WinUI 产品壳不构造 `DesktopShellIconPositionWriter`，真实 Writer / Recovery 工具不暴露在产品 UI 中。

### 3.2 DesktopAgent Diagnostics Workbench

工程诊断工作台，当前源码路径仍为 `src/DesktopAgent.Poc`，进程身份为 `DesktopAgent.Diagnostics.exe`。

职责：

- 显示器与 Shell 图标原始读取；
- Snapshot / SQLite / Recovery 诊断；
- Writer / Recovery 受控实机验收；
- M3 / M4 monitoring、notification、automation diagnostics；
- Debug fault injection 与 acceptance harness。

普通 Diagnostics 启动为前台工程工具，关闭主窗口后退出，不建立第二套产品常驻托盘。工程阶段编号、内部对象名和底层诊断信息可以继续留在这里。

## 4. 冻结技术路线

### 4.1 平台与技术栈

- Windows 11 x64；
- .NET 10；
- WinUI 3 / Windows App SDK：正式产品 UI；
- WPF：Diagnostics Workbench；
- Windows Shell COM / Win32：`DesktopAgent.WindowsInterop`；
- SQLite：模板、规则、待确认、历史与恢复相关持久化；
- JSON + DPAPI：产品设置与 AI Secret；
- xUnit；
- GitHub Actions；
- GPL-3.0-or-later。

### 4.2 分层约束

```text
DesktopAgent.Core
├─ 领域模型
├─ 模板 / 分区 / 规则 / 分类
├─ 规划与恢复合同
├─ 自动化策略与安全合同
└─ 不依赖 UI、Shell COM 或 SQLite

DesktopAgent.Application
├─ 工作流协调
├─ 产品编辑草稿与保存协调
└─ 不直接实现 Windows Writer

DesktopAgent.WindowsInterop
├─ Win32 显示器拓扑
├─ Shell 桌面读取
├─ Shell Writer
├─ Desktop Overlay
├─ Windows 事件适配
└─ 安全执行协调器

DesktopAgent.Persistence
├─ SQLite Store
├─ Schema 迁移
├─ JSON 设置
└─ DPAPI Secret

DesktopAgent.WinUI
└─ Tessel 正式产品 Surface

DesktopAgent.Diagnostics
└─ 工程诊断 / acceptance Surface
```

必须长期保持：

- Core 不引用 WinUI、WPF、Shell COM 或 SQLite 类型；
- 正式 UI 不直接调用 Writer；
- AI 不输出并执行物理坐标；
- Shell COM 与 Win32 能力集中在 WindowsInterop；
- 持久化层不承载布局算法；
- 所有真实写入都必须经过正式计划、授权和安全协调器。

## 5. 冻结的安全链路

```text
当前 Windows / Explorer 事实
        ↓
完整桌面观察
        ↓
模板 + 规则 + 用户授权
        ↓
确定性规划
        ↓
可视预览 / 待确认
        ↓
自动化策略与安全 Gate
        ↓
执行前重新观察
        ↓
重新规划
        ↓
安全快照保存并回读
        ↓
Writer 受控执行
        ↓
独立重新读取验证
        ↓
完成 / 保守回滚 / 人工恢复
```

任何新功能不得绕过以下不变量：

1. 读取不完整时禁止进入真实写入；
2. StableId 与 DeviceName 使用确定性精确匹配；
3. 自动排列等不安全 Shell 状态存在时拒绝写入；
4. 不缓存旧 PIDL 作为未来写入身份；
5. 真实执行前必须 fresh observe / fresh replan；
6. Writer 前必须保存并回读验证完整安全快照；
7. Writer 异常视为可能已经产生副作用；
8. 身份或前置位置不可信时禁止盲目回滚；
9. 位置不确定或回滚不完整时熔断后续自动写入；
10. 独立读回验证属于完成条件；
11. Safety 与 Layout Editing 不能被自动化方式绕过；
12. 仅由 AI 给出的判断不能直接进入 Writer AUTO；
13. 宁可停止整理，也不冒险继续执行。

## 6. 自动化产品原则

用户确认的是：

> **以后什么可以自动做，而不是每次移动都确认。**

当前三种产品自动化方式：

| 方式 | 核心语义 |
|---|---|
| 手动 | 当前明确请求可以执行，其余候选进入待确认 |
| 辅助 | 用户已经确认的长期策略可以自动通过；默认方式 |
| 自动 | 本地确定性规则也可以自动通过 |

无论选择哪一种：

- Safety 永远可以阻止执行；
- 编辑布局时自动化暂停；
- AI-only 建议不会直接写桌面；
- Direct User Request 不需要重复确认同一个动作；
- 已确认长期策略代表未来授权，不代表保存旧 Writer payload。

## 7. 总体里程碑

| 阶段 | 目标 | 当前状态 |
|---|---|---|
| M0 | 工程基础、仓库、测试与 CI | COMPLETE |
| M1 | Explorer 桌面安全读写与恢复底座 | COMPLETE |
| M2 | 模板、规则与手动完整整理闭环 | COMPLETE |
| M3 | 后台感知、建议与受控自动化 | COMPLETE |
| M4 | 通用规则、AI 建议与自然语言配置 | COMPLETE / FROZEN |
| M5-00 | WinUI 3 产品壳可行性 | COMPLETE |
| M5-01 | Automation Policy | COMPLETE |
| M5-02 | Pending Queue / Confirm / Remember | COMPLETE |
| M5-03 | WinUI Product Shell | COMPLETE |
| M5-04 | First-run / Settings / AI Config | COMPLETE |
| M5-05 | Daily Workflow / Layout Management | COMPLETE |
| M5-06 | Diagnostics / Workbench Split | COMPLETE |
| M5-07 | Performance / Long-run Stability + Resident Optimization | IN PROGRESS |
| M5-07.5 | Desktop Zone Editing | PLANNED |
| M5-08 | Packaging / Install / Upgrade | PLANNED |
| M5-09 | Product E2E / RC | PLANNED |
| v2.0 | 真实文件系统整理 | EXPLORATION |

## 8. M0 至 M4：核心能力冻结

M0–M4 已完成并进入 v1.0 Core Feature Freeze。

核心成果包括：

- 多显示器与 Explorer 图标完整读取；
- 布局快照、恢复计划、执行计划和安全 Writer 底座；
- 模板、分区、规则和确定性布局规划；
- 跨重启恢复与保守安全语义；
- 后台变化感知、建议、通知和自动化协调；
- 用户规则、本地确定性规则和 AI 建议的分层决策；
- 自然语言到结构化规则草稿；
- Writer 永远位于 AI 与 UI 之后的受控执行边界。

Core Feature Freeze 的含义是：M5 产品化阶段优先收敛产品体验、稳定性、包装和发布，不继续向核心执行链塞入新的大能力。

## 9. M5 产品化阶段

### M5-00：WinUI 3 Feasibility

**COMPLETE。**

WinUI 3 / Windows App SDK 已通过真实 Windows 生命周期 Gate，正式产品 UI 采用 WinUI 3。WPF 不再承担正式产品壳职责。

### M5-01：Automation Policy

**COMPLETE。**

冻结 Manual / Assisted / Automatic 与授权依据矩阵。Safety、Layout Editing、无有效执行决策、未解决状态等高优先级 Gate 不可被模式覆盖。

### M5-02：Pending Queue / Confirm / Remember

**COMPLETE。**

待确认队列只持久化事实、分类快照、授权依据和原因。Confirm Once 是一次性直接授权；Confirm & Remember 写入既有规则 / profile store，不建立第二套权限数据库。

### M5-03：WinUI Product Shell

**COMPLETE。**

正式产品壳具备 NavigationView、单实例、关闭到托盘、显式退出和待确认只读产品页，并通过真实 Windows Gate。

### M5-04：First-run / Settings / AI Config

**COMPLETE。**

产品设置、首次运行、自动化方式和 OpenAI 兼容 AI Provider 配置完成。API Key 使用 Windows 当前用户范围 DPAPI 保存，不写入普通设置文件。

### M5-05：Daily Workflow / Layout Management

**COMPLETE。**

正式产品具备布局列表、桌面画布、分区预设、影响预览、模板与规则原子保存、当前布局偏好、Desktop Overlay 和可配置逐分区颜色。

当前产品布局工作流仍是观察 / 编辑 / 预览 / 保存，不因为选择布局或显示 Overlay 直接触发真实 Writer。

### M5-06：Diagnostics / Workbench Split

**COMPLETE。**

WinUI 正式产品与 WPF Diagnostics 在进程身份、托盘常驻职责和 UI 语义上拆分完成。Diagnostics 普通启动不保留第二套 tray，关闭主窗口退出；WinUI 继续作为唯一产品常驻 Surface。

### M5-07：Performance / Long-run Stability + Resident Optimization

**IN PROGRESS。**

先建立未优化版本的真实稳定性 baseline：

- Explorer / taskbar 重启后的 tray re-registration；
- 空闲 CPU；
- Working Set / Private Memory；
- Handle / Thread；
- GDI / USER Objects；
- 30 分钟 idle baseline；
- 重复交互 soak；
- 至少 2 小时 resident soak。

首轮 3 分钟 Windows smoke 与 30 分钟 idle baseline 已通过。30 分钟 baseline 中 Private Memory 保持稳定，Working Set 后续被系统明显 trim；Handle 存在会回收的锯齿，因此继续通过 repeated interaction 与 2 小时 soak 验证长期低水位是否稳定。

稳定性 baseline 完成后，再进入 Resident Lifecycle Optimization。目标不是单纯追求任务管理器里的最低数字，而是区分不同运行状态并优化生命周期：

```text
Deep Resident
    ↓ tray interaction
Prewarm
    ↓ open
Interactive
    ↓ feature intent
Active Tool
    ↓ close to tray
Warm Resident
    ↓ timeout / memory pressure
Deep Resident
```

重点方向：

- 托盘长期驻留只保留真正必要的 resident core；
- 打开产品后优先保证完整和流畅的交互体验；
- 重页面与昂贵 native/service 能力按真实意图加载；
- 托盘菜单、PointerPressed 和合适的 hover dwell 可作为无副作用的预热信号；
- 关闭到托盘后先保留短暂 Warm Resident，再逐步收敛到 Deep Resident；
- 不通过频繁 `GC.Collect()` 或强制 Working Set trim 伪造“低内存”；
- 同时测量 resident footprint、恢复耗时和首次页面打开延迟，避免把启动成本偷偷转移到用户第一次点击。

### M5-07.5：Desktop Zone Editing

**PLANNED。**

在 M5-07 稳定性与 resident 生命周期收敛后，补齐直接在原生桌面编辑布局分区的产品能力。

第一版目标：

- 从 Layout / Overlay 明确进入 Desktop Zone Edit Mode；
- 直接在桌面移动、缩放、新建和删除 Zone；
- 支持 Zone 名称、颜色以及必要的边缘 / Zone 吸附；
- 保存后回到普通 click-through Overlay；
- 用户可以完全手动把 Explorer 桌面图标拖入这些区域，不要求启用自动整理。

安全边界：

- Zone Edit Mode 属于 Layout Editing；
- 编辑期间 Automation BLOCK；
- 进入、拖动、缩放、保存、取消均不移动桌面图标；
- Desktop Overlay 的被动显示职责与 Desktop Zone Editor 的交互职责分开；
- 任何后续自动整理仍必须经过 fresh observe / fresh replan / authorization / Safety / Writer 链。

后续增强可以探索“边框和 resize handle 可交互、Zone 内部继续 click-through”的混合命中方式，让用户边调分区边手动拖图标，但不把它作为第一版 Gate。

### M5-08：Packaging / Install / Upgrade

**PLANNED。**

目标：

- 标准 Windows x64 安装方式；
- 安装、覆盖升级和卸载行为；
- Windows App SDK runtime / deployment 策略；
- 可选开机启动；
- 用户数据保留与升级兼容；
- SQLite schema 向前迁移与失败保护；
- Release artifact 与校验文件；
- Tessel 的正式产品身份、图标、开始菜单、托盘 Tooltip 与安装器文案。

Packaging 决策不得通过“打包方便”为理由削弱现有安全和数据边界。

### M5-09：Product E2E / RC

**PLANNED。**

目标是在真实安装环境完成 v1.0 Release Candidate 的端到端验收：

- 首次安装与首次运行；
- 托盘生命周期；
- 设置、布局、待确认与 AI 配置；
- Desktop Zone Editing；
- 重启与升级；
- 长时间驻留；
- Diagnostics 可诊断性；
- 数据兼容与失败路径；
- 最终安全边界检查。

## 10. v1.0 发布完成标准

v1.0 进入正式发布前至少满足：

- 普通用户无需开发环境即可安装和运行；
- 正式 WinUI 产品壳与 Diagnostics 工程工具明确分离；
- 自动化授权语义可解释且不可绕过 Safety；
- AI 关闭或不可用时本地规则与布局能力仍可工作；
- 用户能够直接在桌面编辑 Zone，并在完全不启用自动整理的情况下使用 Overlay 手动整理；
- 长期托盘驻留无明显资源无界增长；
- Explorer 重启后产品能够恢复必要 shell presence；
- 安装、升级和卸载行为可预测；
- 用户数据迁移失败时不会继续进入不安全 Writer；
- 发布产物、版本、校验和许可证信息完整；
- 产品明确承诺 v1.x 不移动、删除或重命名真实文件。

## 11. v2.0 探索方向

v2.0 才考虑真实文件系统整理，包括：

- 真实文件分类和目录规则；
- 文件移动、归档和批量撤销；
- 同名冲突；
- 跨卷复制与校验；
- 文件占用；
- 云盘占位文件；
- 重解析点和符号链接；
- 持久化文件事务日志；
- 跨进程与跨重启文件恢复；
- 回收站与权限边界。

进入 v2.0 前必须设计独立的文件系统 Plan / Transaction / Verification / Recovery，不得复用“只改变桌面图标坐标”的低风险假设。

## 12. 当前明确不做

v1.0 主线不实施：

- 覆盖式或替代式桌面；
- AI 直接调用 Writer；
- 无安全快照的真实写入；
- 未经用户授权的后台自动执行；
- v1.x 中移动、删除或重命名真实文件；
- 云同步与跨设备同步；
- 插件系统；
- 为 UI 或 Packaging 便利让 Core 依赖平台 UI 框架。

## 13. 开发与合并流程

1. 一个明确阶段或用户可见闭环使用独立功能分支；
2. 实现、测试、修复与全量复测在功能分支完成；
3. 使用 Draft PR；
4. 核对真实 diff、范围、自动化测试与 Windows 物理 Gate；
5. 真实 Writer、Recovery、数据库破坏性操作和主线合并继续由人工 Gate 控制；
6. 不在 main 直接开发；
7. 不对共享分支 force-push / rebase；
8. 没有明确授权不 Ready、不 merge stacked Draft PR。

## 14. 路线变更规则

以下变化必须人工确认：

- v1.0 产品定位或真实文件边界；
- Writer / Recovery 安全链；
- 自动化授权语义；
- AI 是否可以越过结构化规则与本地安全层；
- 正式产品与 Diagnostics 的职责边界；
- v2.0 是否正式立项。

以下内容可以在阶段内部调整：

- 类型和类名；
- PR 拆分数量；
- 具体 Windows API；
- 具体模型 / 服务提供方；
- UI 控件与文案；
- 安装工具；
- 不改变外部语义的性能实现。

## 15. 修订记录

| 日期 | 说明 |
|---|---|
| 2026-08-04 | 首次冻结 M0–M5、WinUI 3 决策门与 v2.0 文件整理探索方向 |
| 2026-08-12 | 同步 M0–M4 Core Freeze、M5-00 至 M5-06 完成状态、WinUI / Diagnostics 双 Surface 与 M5-07 当前路线 |
| 2026-08-12 | 确定对外产品名 Tessel，记录 Resident Lifecycle Optimization，并在 Packaging 前增加 M5-07.5 Desktop Zone Editing |