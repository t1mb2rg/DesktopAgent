# DesktopAgent

DesktopAgent 是一个面向 Windows 11 的本地桌面管理 Agent。目标是在不接管 Explorer 原生桌面的前提下，通过确定性规则与可选 AI 建议理解桌面项目用途，提供分区、布局、待确认工作流，并在安全策略允许时进入受控执行链。

> 当前状态：**v1.0 Core Feature Freeze 已激活；M5-05 Daily Workflow + Layout Management 已完成，正在推进 M5-06 Diagnostics / Workbench Split。**

## 当前产品形态

DesktopAgent 现在明确分为两个 Surface。

### DesktopAgent.WinUI

正式产品壳，面向日常使用：

- 概览 / AI 助手 / 待确认 / 布局 / 设置；
- 单实例与 Windows 托盘常驻；
- 关闭主窗口后继续驻留，托盘可直接打开页面或退出；
- 布局模板、主动布局偏好、Fresh Impact Preview；
- Desktop Zone Overlay，支持 Light / Standard / Strong / Custom；
- Ring ColorPicker、全局颜色和逐 Zone 独立颜色；
- Pending Queue 只读产品页；
- Automation Mode 与 AI Provider 设置。

正式产品壳不会直接构造 `DesktopShellIconPositionWriter`，不会因为 AI 建议、被动选择或 Overlay 展示直接移动真实桌面图标。

### DesktopAgent Diagnostics Workbench

工程诊断工作台。历史源码路径暂时仍为 `src/DesktopAgent.Poc`，M5-06 起输出进程身份为 `DesktopAgent.Diagnostics`。

它保留：

- 显示器拓扑与 Shell 图标原始读取；
- Snapshot / SQLite / Recovery 诊断；
- 受控 Writer / Recovery 实机验收；
- M3/M4 monitoring / notification / automation diagnostics；
- Debug fault injection 与 acceptance harness。

Diagnostics Workbench 不是正式产品 UI。普通启动作为前台工程工具运行，不建立第二套产品常驻 tray；只有显式 Debug unattended acceptance 场景才保留旧 M3 resident tray。

详细边界见 [`docs/experiments/M5-06-diagnostics-workbench-split.md`](docs/experiments/M5-06-diagnostics-workbench-split.md)。

## 技术路线

- .NET 10
- 正式产品 UI：WinUI 3 / Windows App SDK
- 工程诊断工作台：WPF
- Windows Shell COM / Win32 集中在 `DesktopAgent.WindowsInterop`
- 核心业务与安全合同不依赖 UI
- SQLite 本地数据
- 用户自带 OpenAI 兼容 Provider 配置
- GPL-3.0-or-later

## 项目结构

```text
DesktopAgent/
├─ src/
│  ├─ DesktopAgent.Core/             # 领域模型 / 安全合同
│  ├─ DesktopAgent.Application/      # 应用协调与工作流
│  ├─ DesktopAgent.WindowsInterop/   # Shell COM / Win32
│  ├─ DesktopAgent.Persistence/      # SQLite / JSON / DPAPI
│  ├─ DesktopAgent.WinUI/            # 正式产品壳
│  └─ DesktopAgent.Poc/              # 历史路径；当前为 Diagnostics Workbench
├─ tests/
│  └─ DesktopAgent.Tests/
├─ docs/
│  ├─ decisions/
│  ├─ experiments/
│  └─ milestones/
└─ .github/workflows/
```

## 本地运行

要求：Windows 11、.NET 10 SDK，以及可构建 WPF / WinUI 3 的 Windows 开发环境。

### 正式产品壳

```powershell
dotnet run --project .\src\DesktopAgent.WinUI\DesktopAgent.WinUI.csproj -c Debug
```

### Diagnostics Workbench

```powershell
dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj -c Debug
```

M5-06 起该项目构建的可执行文件名为 `DesktopAgent.Diagnostics.exe`；项目路径 / namespace 暂时保留历史 `DesktopAgent.Poc` 名称以降低迁移回归面。

### 全仓验证

```powershell
dotnet restore DesktopAgent.sln
dotnet build DesktopAgent.sln -c Debug
dotnet build DesktopAgent.sln -c Release
dotnet test tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Release --no-build
```

## 当前验证基线

最近一次真正启动并完整执行的 GitHub Actions 为 **#344**：

- Restore PASS
- Debug build PASS
- Release build PASS
- Release tests PASS
- **1841 / 1841 PASS**
- 121 条既有 baseline warnings
- 0 errors

后续 Actions 因 GitHub billing / spending-limit 外部 Gate 无法启动 runner，因此最新 M5-05 / M5-06 代码暂由本机 build 与真实 Windows Gate 承担 freshness validation。外部 CI 阻塞不视为代码失败。

## 已冻结的安全原则

- AI 和 UI 不直接调用 Shell Writer；
- Layout Editing 阶段 Automation Policy 必须 BLOCK；
- Preview 每次基于 Fresh Desktop Observation；
- 不持久化并重放旧 WriterCommand；
- Safety / Layout Editing 永远不能被 Automation Mode 绕过；
- AI-only authority 永远不能直接进入 Writer AUTO；
- Pending Queue 只持久化事实、分类快照与授权依据，不持久化执行 payload；
- 真实执行前必须 fresh observe / fresh replan；
- Writer 异常视为可能已产生副作用；
- 身份或前置位置不可信时禁止盲目回滚；
- 宁可停止整理，也不冒险继续执行。

## M5 路线

当前产品化阶段按以下顺序推进：

```text
M5-00  WinUI Feasibility                  COMPLETE
M5-01  Automation Policy                  COMPLETE
M5-02  Pending Queue / Confirm / Remember COMPLETE
M5-03  WinUI Product Shell                COMPLETE
M5-04  First-run / Settings / AI Config   COMPLETE
M5-05  Daily Workflow / Layout Management COMPLETE
M5-06  Diagnostics / Workbench Split      ACTIVE
M5-07  Performance / Long-run Stability   NEXT
M5-08  Packaging / Install / Upgrade
M5-09  Product E2E / RC
```

M5-06 不新增 Writer 能力，重点是让正式产品壳与工程诊断工具在身份、常驻职责和 UI 语义上彻底分开。
