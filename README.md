# Tessel

**Tessel 是一个 Windows 11 桌面整理工具。**

它直接使用 Explorer 原生桌面，通过布局、分区和规则，让桌面按照你确认过的习惯保持有序。

Tessel 不替换 Windows 桌面，也不移动真实文件。AI 可以参与分类、规则和整理方案的生成，但不会因为一次判断就直接改动桌面。真正的桌面写入始终位于明确授权和安全检查之后。

> 当前正在进行 **v1.0 产品化**。核心能力已经冻结，现阶段主要处理产品界面、长期稳定性、性能、安装与发布。

## 它现在能做什么

### 布局

可以为桌面保存不同的布局模板，为屏幕划分区域，并根据当前桌面状态查看布局会产生什么影响。

布局编辑和真实桌面执行是两件不同的事。选择、编辑或保存一个布局不会直接移动图标。

### 桌面线框

Tessel 可以直接在原生 Windows 桌面上显示布局分区。

线框支持边框、填充、圆角以及全局和逐分区颜色，并保持 click-through，不抢焦点，也不会取代 Explorer 桌面。

### 待确认

需要你决定的事情会进入待确认列表。

你可以只确认这一次，也可以把某种习惯记下来，让之后符合条件的操作不再重复询问。

> **用户确认的是“以后什么可以自动做”，不是每次移动都确认。**

### 自动化

Tessel 提供手动、辅助和自动三种方式。

自动化方式决定哪些已经满足条件的操作可以继续向前，但不会关闭安全检查。布局正在编辑、桌面状态不可信或者执行条件不完整时，Tessel 仍然会停止。

### AI

可以连接 OpenAI 兼容的 AI 服务，用来理解分类、规则和自然语言整理意图。

AI 可以提出方案，但不会直接获得桌面 Writer 权限。

API Key 保存在当前 Windows 用户的加密存储中，不写进普通设置文件。

## Tessel 和 Diagnostics

项目现在有两个明确分开的界面。

### Tessel

源码位于：

```text
src/DesktopAgent.WinUI
```

这是正式产品。

它负责日常使用的概览、待确认、布局、设置和 AI 入口，采用单实例运行，并在关闭主窗口后继续驻留 Windows 托盘。

正式产品不会把 COM、SQLite、Recovery、Writer 等工程调试界面暴露给普通用户。

### DesktopAgent Diagnostics Workbench

目前源码仍保留历史路径：

```text
src/DesktopAgent.Poc
```

生成的进程目前为：

```text
DesktopAgent.Diagnostics.exe
```

它是工程诊断和受控实机验收工具。

这里可以看到 Shell 原始状态、SQLite、Snapshot、Recovery、Writer、fault injection 和其他底层信息。普通启动时它只是一个前台工具，关闭主窗口后退出，不建立第二套产品常驻托盘。

现阶段 `DesktopAgent.*` 仍然作为内部工程名保留。产品品牌改成 Tessel 不意味着必须把稳定的 namespace 和源码路径全部重命名。

## 为什么不直接让 AI 整理桌面

桌面图标移动看起来很简单，但它实际上会跨过 Explorer、Shell COM、持久化状态和真实 Windows 副作用。

一旦写入已经开始，程序崩溃或者 Shell 状态发生变化，就不能假设“什么都没有发生”。

所以 Tessel 把 AI、规划和真实执行分开。

```text
当前桌面状态
      ↓
布局 / 规则 / 用户意图
      ↓
生成方案
      ↓
预览 / 待确认
      ↓
自动化与安全检查
      ↓
重新读取当前桌面
      ↓
重新规划
      ↓
受控执行
      ↓
重新读取并验证结果
```

其中几条规则不会因为自动化程度提高而改变：

- AI 不直接调用 Shell Writer；
- 真实执行前必须重新观察当前桌面并重新规划；
- 不保存旧的 WriterCommand 等着以后直接重放；
- 编辑布局时自动化暂停；
- Safety 永远可以否决执行；
- Writer 异常视为可能已经产生了真实副作用；
- 无法确认身份、位置或恢复条件时停止执行，而不是猜。

简单来说，**宁可这次不整理，也不要拿真实桌面赌一次。**

## 技术栈

```text
Windows 11 x64
.NET 10

WinUI 3 / Windows App SDK
    正式产品 UI

WPF
    Diagnostics Workbench

Windows Shell COM / Win32
    原生桌面读取、Overlay 与受控 Writer

SQLite
    模板、规则、Pending、Recovery 等持久化

JSON + DPAPI
    产品设置与 AI Secret

xUnit
GitHub Actions
GPL-3.0-or-later
```

核心业务、安全合同和自动化策略不依赖具体 UI 框架。

## 项目结构

```text
DesktopAgent/
├─ src/
│  ├─ DesktopAgent.Core/
│  │     领域模型、规划与安全合同
│  │
│  ├─ DesktopAgent.Application/
│  │     应用协调与产品工作流
│  │
│  ├─ DesktopAgent.WindowsInterop/
│  │     Shell COM、Win32、Overlay 与 Windows 能力
│  │
│  ├─ DesktopAgent.Persistence/
│  │     SQLite、JSON 与 DPAPI
│  │
│  ├─ DesktopAgent.WinUI/
│  │     Tessel 正式产品
│  │
│  └─ DesktopAgent.Poc/
│        历史路径，当前为 Diagnostics Workbench
│
├─ tests/
│  └─ DesktopAgent.Tests/
│
├─ tools/
│     工程验证与性能采样工具
│
└─ docs/
      设计、决策、实验、里程碑与路线图
```

## 本地运行

要求 Windows 11、.NET 10 SDK，以及可以构建 WinUI 3 / WPF 的 Windows 开发环境。

### Tessel

```powershell
dotnet run --project .\src\DesktopAgent.WinUI\DesktopAgent.WinUI.csproj -c Debug
```

### Diagnostics

```powershell
dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj -c Debug
```

### 全仓验证

```powershell
dotnet restore .\DesktopAgent.sln
dotnet build .\DesktopAgent.sln -c Debug
dotnet build .\DesktopAgent.sln -c Release
dotnet test .\DesktopAgent.sln -c Debug --no-build
```

## 当前进度

M0 到 M4 的核心能力已经完成，并进入：

```text
v1.0 CORE FEATURE FREEZE
```

现在正在进行 M5 产品化。

```text
M5-00   WinUI Feasibility                  COMPLETE
M5-01   Automation Policy                  COMPLETE
M5-02   Pending Queue / Confirm / Remember COMPLETE
M5-03   WinUI Product Shell                COMPLETE
M5-04   First-run / Settings / AI Config   COMPLETE
M5-05   Daily Workflow / Layout Management COMPLETE
M5-06   Diagnostics / Workbench Split      COMPLETE
M5-07   Performance / Long-run Stability   IN PROGRESS
M5-07.5 Desktop Zone Editing               PLANNED
M5-08   Packaging / Install / Upgrade      PLANNED
M5-09   Product E2E / RC                   PLANNED
```

M5-07 当前主要验证长期托盘驻留、空闲资源占用、重复交互、Explorer 重启后的托盘恢复，以及更长时间运行后的资源趋势。

目前 3 分钟采样和 30 分钟 idle baseline 已通过。接下来继续验证重复交互和长时间 resident soak，然后再以这些数据为基线处理启动与托盘驻留时的资源占用。

Desktop Zone Editing 仍是后续规划能力，不属于“现在能做什么”。目标是在不授予任何新 Writer 权限的前提下，让用户直接在原生桌面调整分区，再选择手动整理或进入现有的规则与授权流程。

## v1.0 的边界

v1.0 只整理 **Explorer 桌面图标的位置和布局**。

它不会：

```text
移动真实文件
删除文件
重命名文件
扫描整个磁盘
替换 Windows 桌面
让 AI 绕过安全执行链
```

真实文件分类、移动、归档和文件系统事务恢复属于之后独立讨论的方向，不会因为桌面图标整理已经安全就顺手塞进 v1.0。

完整的阶段规划和工程边界见 [`docs/roadmap/DesktopAgent-product-roadmap.md`](docs/roadmap/DesktopAgent-product-roadmap.md)。
