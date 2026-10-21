# Tessel 开发文档

面向贡献者与维护者。用户使用说明见 [README](../README.md)。

> 仓库名仍为 `DesktopAgent`（历史命名），产品名是 **Tessel**。代码命名空间、项目名与
> 发布产物按此映射：产品壳 `DesktopAgent.WinUI` → 安装后 `Tessel`。

## 产品形态

Tessel 明确分为两个 Surface。

### DesktopAgent.WinUI —— 正式产品壳

面向日常使用：桌面 / 设计库 / AI 助手 / 待处理 / 设置；单实例与 Windows 托盘常驻；
关闭主窗口后继续驻留，托盘可直接打开页面或退出。

- AI 对话式工作区设计（理解一次，之后轻量对话调整）；
- 设计库：保存、按版本查看、删除；
- 单屏布局模板与 Fresh Impact Preview；
- Desktop Zone Overlay，支持 Light / Standard / Strong / Custom，改外观后线框实时生效；
- Ring ColorPicker、全局颜色和逐 Zone 独立颜色；
- Pending Queue 只读产品页；
- Automation Mode 与 AI Provider 设置。

正式产品壳**不会**直接构造 `DesktopShellIconPositionWriter`，不会因为 AI 建议、被动选择或
Overlay 展示直接移动真实桌面图标。

### DesktopAgent Diagnostics Workbench —— 工程诊断工作台

历史源码路径暂时仍为 `src/DesktopAgent.Poc`，M5-06 起输出进程身份为 `DesktopAgent.Diagnostics`。
保留显示器拓扑与 Shell 图标原始读取、Snapshot / SQLite / Recovery 诊断、受控 Writer /
Recovery 实机验收、M3/M4 monitoring / notification / automation diagnostics、Debug fault
injection 与 acceptance harness。

Diagnostics Workbench 不是正式产品 UI；普通启动作为前台工程工具运行，不建立第二套产品常驻
tray，只有显式 Debug unattended acceptance 场景才保留旧 M3 resident tray。

详细边界见 [`experiments/M5-06-diagnostics-workbench-split.md`](experiments/M5-06-diagnostics-workbench-split.md)。

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
├─ tools/
│  ├─ publish.ps1                    # 一键构建 + 测试 + 打包 + 安装器
│  ├─ installer/tessel.iss           # Inno Setup 脚本
│  └─ uia-acceptance/                # 界面验收用的 UIA 驱动脚本
├─ docs/
│  ├─ decisions/                     # 关键技术决策
│  ├─ experiments/                   # 逐轮实验与真机验收记录
│  ├─ milestones/                    # 里程碑
│  ├─ roadmap/
│  └─ suggestions/                   # 产品反馈清单
└─ .github/workflows/                # build.yml / release.yml
```

## 本地运行

要求：Windows 11、.NET 10 SDK，以及可构建 WPF / WinUI 3 的 Windows 开发环境。

产品使用前必须完成全部当前显示器的真实网格校准。启动时会核验已保存的实测记录；缺失、
部分完成或显示环境变化时停留在校准引导。校准需要单独确认，取消不能跳过。校准成功**不等于**
授予无人值守移动权限。

```powershell
# 正式产品壳
dotnet run --project .\src\DesktopAgent.WinUI\DesktopAgent.WinUI.csproj -c Debug

# Diagnostics Workbench（产物名 DesktopAgent.Diagnostics.exe）
dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj -c Debug
```

注意：产品壳必须用 **Debug + x64** 构建，否则渠道会变成 Stable（`%LOCALAPPDATA%\Tessel`），
与开发数据（`%LOCALAPPDATA%\Tessel.Dev`）不在同一个库。

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
| `Tessel-<版本>-win-x64\` | 自包含发布目录（约 184 MB），解压即用 |
| `Tessel-<版本>-win-x64.zip` | 绿色包（约 70 MB） |
| `Tessel-Setup-<版本>.exe` | 安装器（约 48 MB，需要 Inno Setup 6） |

常用开关：`-SkipTests`、`-SkipTrim`、`-SkipInstaller`、`-Version x.y.z`、`-Launch`、`-IsccPath`。

裁剪步骤会删除 28 个文件约 42 MB 的 WinAppSDK ML 组件（`onnxruntime.dll`、`DirectML.dll`、
`Microsoft.Windows.AI.*` 等），产品不使用这些组件。

### 发布流程

推 `v*` tag 触发 `.github/workflows/release.yml`：build → test → self-contained publish →
trim → zip → Inno Setup → 从 `CHANGELOG.md` 截取对应版本段落作为 release notes →
上传 `softprops/action-gh-release`。

注意：workflow 里 build / test **不要**传 `Platform` 属性，只在 publish 传；否则
`dotnet test --no-build` 找不到 `tests\...\bin\x64\Release\...\DesktopAgent.Tests.dll`。

## 当前验证基线

- Release 全量测试 **2587 / 2587 通过**（0 失败、0 跳过）；
- GitHub Actions `build` 工作流：restore → Debug 构建 → Release 构建 → Release 测试；
- 真机验收在 Windows 11 三屏环境完成：校准往返、跨屏汇聚与一键回滚、设计库增删、
  线框外观实时刷新、安装 → 启动 → 卸载。

历史 Actions 曾因 GitHub billing / spending-limit 外部 Gate 无法启动 runner，那段时间的代码
由本机 build 与真实 Windows 验证承担；当前远端 CI 已恢复正常。

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

## 文档索引

| 目录 | 内容 |
| --- | --- |
| [`decisions/`](decisions/) | 关键技术决策与其理由 |
| [`experiments/`](experiments/) | 逐轮实验记录，含真机验收证据与失败复盘（94 篇） |
| [`milestones/`](milestones/) | 里程碑定义 |
| [`suggestions/`](suggestions/) | 产品反馈清单（F-n）与处置状态 |
