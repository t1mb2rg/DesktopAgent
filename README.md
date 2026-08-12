# DesktopAgent

DesktopAgent 是一个面向 Windows 11 的本地桌面布局与自动整理工具。

它直接理解和管理 Explorer 原生桌面图标的位置，不替换桌面、不移动真实文件，也不会因为 AI 建议就直接执行桌面写入。产品目标是让桌面按用户长期确认的习惯保持有序，同时把真实执行始终放在明确的授权和安全边界之后。

> 当前状态：**v1.0 Core Feature Freeze 已激活；M5-00 至 M5-06 已完成，正在推进 M5-07 Performance / Long-run Stability。**

## 现在的 DesktopAgent

正式产品目前提供：

- WinUI 3 产品壳：概览、AI 助手、待确认、布局、设置；
- 单实例与 Windows 托盘常驻，关闭主窗口后继续在后台运行；
- 布局模板、分区预设、当前布局偏好与基于实时桌面状态的影响预览；
- 桌面分区预览层，可配置边框、填充、圆角、全局颜色和逐分区颜色；
- 待确认队列，只保存事实、分类结果和授权依据，不保存可重放的桌面写入命令；
- 手动、辅助、自动三种自动化方式；
- 可选 OpenAI 兼容 AI 服务配置，API Key 使用 Windows 当前用户范围的 DPAPI 加密保存。

当前正式 WinUI 产品壳仍以**观察、预览、配置和保存偏好**为主，不直接构造真实桌面 Writer。已有 Writer / Recovery 能力继续保留在工程诊断工作台和既有安全 Gate 中，等待后续产品执行链正式接入。

## 两个明确分开的界面

### DesktopAgent

`src/DesktopAgent.WinUI`

正式产品，面向日常使用：

- 单实例；
- 常驻托盘；
- 关闭窗口不退出；
- 托盘可打开概览、布局、待确认、设置或退出；
- 不暴露 COM、SQLite、Recovery 和 Writer 的工程调试面板；
- 不引用 WPF Diagnostics 工程。

### DesktopAgent Diagnostics Workbench

`src/DesktopAgent.Poc`（历史源码路径）

工程诊断与受控验收工具。M5-06 起输出进程身份为 `DesktopAgent.Diagnostics.exe`，普通启动作为前台工具运行，关闭主窗口后退出，不建立第二套产品常驻托盘。

它保留：

- 显示器拓扑与 Shell 图标原始读取；
- Snapshot / SQLite / Recovery 诊断；
- 受控 Writer / Recovery 实机验收；
- M3 / M4 monitoring、notification、automation diagnostics；
- Debug fault injection 与 acceptance harness。

Diagnostics Workbench 不是正式产品 UI。工程术语、阶段编号和底层状态可以继续留在那里，不需要伪装成用户界面。

详细边界见 [`docs/experiments/M5-06-diagnostics-workbench-split.md`](docs/experiments/M5-06-diagnostics-workbench-split.md)。

## 核心安全原则

DesktopAgent 的核心原则不是“尽量自动”，而是：

> 用户确认的是“以后什么可以自动做”，不是每次移动都确认。

长期保持以下不变量：

- AI 和正式产品 UI 不直接调用 Shell Writer；
- 编辑布局时自动化必须暂停；
- 预览和真实执行都必须基于新的桌面观察结果；
- 不持久化并重放旧的 WriterCommand；
- Safety 与 Layout Editing 不能被自动化方式绕过；
- 仅由 AI 给出的判断不能直接进入自动 Writer；
- 待确认队列不保存执行坐标、COM 对象或 Writer payload；
- 真实执行前必须重新观察、重新规划；
- Writer 异常视为可能已经产生副作用；
- 身份或前置位置不可信时禁止盲目回滚；
- 宁可停止整理，也不冒险继续执行。

## 技术路线

- Windows 11 x64
- .NET 10
- WinUI 3 / Windows App SDK 2.3：正式产品 UI
- WPF：Diagnostics Workbench
- Windows Shell COM / Win32：`DesktopAgent.WindowsInterop`
- SQLite：模板、规则、待确认与恢复相关持久化
- JSON + DPAPI：产品设置与 AI Secret
- xUnit
- GitHub Actions
- GPL-3.0-or-later

核心业务、安全合同和自动化策略不依赖 UI 框架。

## 项目结构

```text
DesktopAgent/
├─ src/
│  ├─ DesktopAgent.Core/             # 领域模型 / 规划 / 安全合同
│  ├─ DesktopAgent.Application/      # 应用协调与产品工作流
│  ├─ DesktopAgent.WindowsInterop/   # Shell COM / Win32 / Windows 能力
│  ├─ DesktopAgent.Persistence/      # SQLite / JSON / DPAPI
│  ├─ DesktopAgent.WinUI/            # 正式产品壳
│  └─ DesktopAgent.Poc/              # 历史路径；当前为 Diagnostics Workbench
├─ tests/
│  └─ DesktopAgent.Tests/
├─ tools/                            # 工程验证与性能采样工具
├─ docs/
│  ├─ decisions/
│  ├─ experiments/
│  ├─ milestones/
│  └─ roadmap/
└─ .github/workflows/
```

## 本地运行

要求：Windows 11、.NET 10 SDK，以及可构建 WinUI 3 / WPF 的 Windows 开发环境。

### 正式产品

```powershell
dotnet run --project .\src\DesktopAgent.WinUI\DesktopAgent.WinUI.csproj -c Debug
```

### Diagnostics Workbench

```powershell
dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj -c Debug
```

项目路径与 namespace 暂时保留历史 `DesktopAgent.Poc` 名称，但生成的可执行文件为 `DesktopAgent.Diagnostics.exe`。

### 全仓验证

```powershell
dotnet restore .\DesktopAgent.sln
dotnet build .\DesktopAgent.sln -c Debug
dotnet build .\DesktopAgent.sln -c Release
dotnet test .\DesktopAgent.sln -c Debug --no-build
```

## 当前验证状态

- M5-06 latest HEAD：本机 full solution Debug build PASS；
- M5-06 latest HEAD：本机 full solution tests PASS；
- M5-06 Physical Windows Gate：PASS；
- WinUI / Diagnostics 双进程职责拆分：PASS；
- M5-07 3 分钟空闲性能 smoke：PASS，暂未发现 CPU、Private Memory、Handle、GDI 或 USER object 泄漏信号；
- 最近一次真正启动 runner 并完整执行的 GitHub Actions 为 #344：1841 / 1841 tests PASS，0 errors；
- 后续 GitHub Actions 因账户 billing / spending-limit 外部 Gate 无法启动 runner，该状态不视为代码失败。

M5-07 正在继续进行 30 分钟 idle baseline、重复交互 soak、Explorer 重启托盘恢复与至少 2 小时 resident soak。

## M5 产品化路线

```text
M5-00  WinUI Feasibility                  COMPLETE
M5-01  Automation Policy                  COMPLETE
M5-02  Pending Queue / Confirm / Remember COMPLETE
M5-03  WinUI Product Shell                COMPLETE
M5-04  First-run / Settings / AI Config   COMPLETE
M5-05  Daily Workflow / Layout Management COMPLETE
M5-06  Diagnostics / Workbench Split      COMPLETE
M5-07  Performance / Long-run Stability   IN PROGRESS
M5-08  Packaging / Install / Upgrade      NEXT
M5-09  Product E2E / RC
```

完整产品路线与历史阶段边界见 [`docs/roadmap/DesktopAgent-product-roadmap.md`](docs/roadmap/DesktopAgent-product-roadmap.md)。
