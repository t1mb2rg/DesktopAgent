# DesktopAgent

DesktopAgent 是一个面向 Windows 11 的本地桌面管理 Agent。目标是在不接管 Explorer 原生桌面的前提下，通过确定性规则与可选 AI 建议理解桌面项目用途，提供分区、布局、待确认工作流，并在安全策略允许时进入受控执行链。

> 当前版本：**1.0.0**。产品主线已改为「以 AI 对话为中心的工作区设计」，具备校准、设计、预览、应用、撤销的完整闭环。
> 变更见 [CHANGELOG.md](CHANGELOG.md)，已知限制见下方「已知限制」。

## 下载与安装

1. 从 [Releases](https://github.com/t1mb2rg/DesktopAgent/releases) 下载 `Tessel-Setup-<版本>.exe`。
2. 双击安装。默认装到当前用户目录（`%LOCALAPPDATA%\Programs\Tessel`），**不需要管理员权限**。
   也可以选择「下载 zip 解压即用」的绿色包。
3. 首次启动会要求**校准全部显示器的桌面网格**：产品会临时创建并移动专用测试图标完成测量，
   需要单独确认；取消或失败不会跳过校准。
4. 校准通过后进入 AI 助手页，描述你想要的桌面样子即可。

校准必须在**当前显示环境下**完成；之后如果改了分辨率、缩放、显示排列或任务栏位置，需要重新校准。

### 数据放在哪

| 内容 | 路径 |
| --- | --- |
| 数据库（设计、会话、恢复检查点） | `%LOCALAPPDATA%\Tessel\desktop-agent.db` |
| 设置与外观 | `%LOCALAPPDATA%\Tessel\settings.json` |
| 校准事实 | `%LOCALAPPDATA%\Tessel\grid-calibration-environment.json` |
| 当前设计草稿 | `%LOCALAPPDATA%\Tessel\workspace-session.json` |
| API Key | Windows DPAPI Secret Store（不落明文） |

从旧版本升级时数据目录不变，直接覆盖安装即可。

### 卸载

设置 → 应用 → 已安装的应用 → Tessel → 卸载，或运行安装目录下的 `unins000.exe`。
卸载**只删除程序本体**，`%LOCALAPPDATA%\Tessel` 下的数据会保留；确认不再需要时手动删除该目录。

## 当前产品形态

DesktopAgent 现在明确分为两个 Surface。

### DesktopAgent.WinUI

正式产品壳，面向日常使用：

- 桌面 / 设计库 / AI 助手 / 待处理 / 设置；
- 单实例与 Windows 托盘常驻；
- 关闭主窗口后继续驻留，托盘可直接打开页面或退出；
- AI 对话式工作区设计（理解一次，之后轻量对话调整）；
- 设计库：保存、按版本查看、删除；
- 单屏布局模板与 Fresh Impact Preview；
- Desktop Zone Overlay，支持 Light / Standard / Strong / Custom，改外观后线框实时生效；
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

产品使用前必须完成全部当前显示器的真实网格校准。启动时会核验已保存的实测记录；缺失、部分完成或显示环境变化时停留在校准引导。校准需要单独确认，取消不能跳过。校准成功不等于授予无人值守移动权限。

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

## 构建与打包

```powershell
# 开发调试
dotnet build DesktopAgent.sln -c Debug -p:Platform=x64

# 全量测试
dotnet test tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Release -p:Platform=x64

# 一条命令出发布包（构建 + 测试 + 自包含发布 + 裁剪 + zip + 安装器）
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Clean
```

`publish.ps1` 产物都在 `artifacts\release\`：

| 产物 | 说明 |
| --- | --- |
| `Tessel-<版本>-win-x64\` | 自包含发布目录，解压即用 |
| `Tessel-<版本>-win-x64.zip` | 绿色包 |
| `Tessel-Setup-<版本>.exe` | 安装器（需要 Inno Setup 6） |

常用开关：`-SkipTests`、`-SkipTrim`、`-SkipInstaller`、`-Version x.y.z`、`-Launch`、`-IsccPath`。

## 当前验证基线

- Release 全量测试 **2587 / 2587 通过**（0 失败、0 跳过）；
- GitHub Actions `build` 工作流：restore → Debug 构建 → Release 构建 → Release 测试；
- 真机验收在 Windows 11 三屏环境完成：校准往返、跨屏汇聚与一键回滚、设计库增删、
  线框外观实时刷新、安装 → 启动 → 卸载。

历史 Actions 曾因 GitHub billing / spending-limit 外部 Gate 无法启动 runner，那段时间的代码由本机
build 与真实 Windows 验证承担；当前远端 CI 已恢复正常。

## 已知限制

- **无人值守自动执行未启用。** 引擎、安全门与「记住这块屏」授权都在代码里，但产品壳默认只到
  「建议 / 协助」级别；开启属于实验特性，需要显式验收钥匙，本轮未纳入发布形态。
- 只有 x64 构建，没有 arm64。
- 没有自动更新；升级方式是重新运行新版安装器覆盖安装。
- 校准会临时创建并移动专用测试图标，必须由用户单独确认。
- 界面与真机验收在 Windows 11 三屏环境完成，其他多屏组合尚未逐一验收。
- 已知崩溃根因未完全定位：极少数情况下「应用到桌面」可能让进程异常退出（`0xc000027b`），
  应用前会保存安全检查点，桌面上的写入是 fail-closed 的；如果遇到请反馈
  `%TEMP%\tessel-product-crash.log`。

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
M5-06  Diagnostics / Workbench Split      COMPLETE
M5-07  Performance / Long-run Stability   NEXT
M5-08  Packaging / Install / Upgrade       COMPLETE (1.0.0)
M5-09  Product E2E / RC                   NEXT
```

M5-06 不新增 Writer 能力，重点是让正式产品壳与工程诊断工具在身份、常驻职责和 UI 语义上彻底分开。
