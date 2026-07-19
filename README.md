# DesktopAgent

DesktopAgent 是一个面向 Windows 11 的本地桌面管理 Agent。它计划通过 AI 理解文件的项目用途，在不接管 Explorer 原生桌面的前提下，为活跃工作文件生成静态分区、提供分类建议，并在用户确认后完成归档与恢复。

> 当前状态：M1 技术验证阶段，仓库暂为私有。

## 已确定的技术路线

- .NET 10 LTS
- WPF 首版界面，架构允许后续增加 WinUI 3 表现层
- Shell COM / 必要的 Win32 API 集中封装在 WindowsInterop
- 核心业务逻辑不依赖 WPF
- 用户自带 OpenAI 兼容 API Key
- SQLite 本地数据
- GPL-3.0-or-later

## 当前 PoC

当前首个实验用于：

1. 枚举任意数量的显示器；
2. 读取虚拟桌面物理像素坐标；
3. 正确处理位于主屏左侧或上方的负坐标；
4. 记录主屏、工作区、DPI 与缩放；
5. 为后续“只规划用户指定显示器”建立坐标基础。

## 项目结构

```text
DesktopAgent/
├─ src/
│  ├─ DesktopAgent.Core/
│  ├─ DesktopAgent.WindowsInterop/
│  └─ DesktopAgent.Poc/
├─ tests/
│  └─ DesktopAgent.Tests/
├─ docs/
│  ├─ decisions/
│  └─ experiments/
└─ .github/workflows/
```

## 本地运行

要求：

- Windows 11
- Visual Studio 2026 或支持 .NET 10 的 Visual Studio
- `.NET 桌面开发` 工作负载
- .NET 10 SDK

```powershell
dotnet restore DesktopAgent.sln
dotnet build DesktopAgent.sln -c Debug
dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj
```

## M1 验收目标

- 枚举任意数量显示器；
- 用户选择一块目标显示器；
- 读取桌面图标及坐标；
- 保存布局快照；
- 将选定图标移动到目标显示器的测试分区；
- 一键恢复；
- 分区层不影响原生桌面右键、框选与拖拽。

## 安全原则

- AI 不直接调用文件系统；
- 文件操作必须由 Application 层生成并校验正式计划；
- 默认不永久删除文件；
- 每次批量操作先保存快照；
- 宁可停止整理，也不冒险继续执行。
