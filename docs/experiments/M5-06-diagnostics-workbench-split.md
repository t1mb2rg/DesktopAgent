# M5-06 Diagnostics / Workbench Split

## 目标

M5-06 把 DesktopAgent 的正式产品壳与工程诊断工作台从产品语义、进程身份和常驻职责上明确拆开。

本阶段不新增桌面 Writer 能力，不修改 Recovery safety semantics，不打开 production unattended gate。

## 两类 Surface

### DesktopAgent.WinUI

正式产品壳，面向日常用户。

职责：

- 概览 / AI 助手 / 待确认 / 布局 / 设置；
- 产品设置与安全的展示偏好；
- 常驻托盘、关闭到托盘、显式退出；
- Pending Queue 读取与产品工作流；
- Desktop Overlay 只读空间提示。

边界：

- 不引用 WPF diagnostics / PoC 工程；
- 不暴露 COM 原始对象、SQLite 原始行、Recovery internals、Writer 调试按钮；
- 不构造 `DesktopShellIconPositionWriter`；
- 不承担 fault injection / acceptance harness。

### DesktopAgent Diagnostics Workbench

工程诊断工作台，当前物理工程路径仍为 `src/DesktopAgent.Poc`，以避免 M5-06 同时进行大规模 namespace / test reference 迁移。

M5-06 起其产品身份改为 `DesktopAgent.Diagnostics`，解决方案显示名改为 `DesktopAgent.Diagnostics`。`DesktopAgent.Poc` 仅保留为源码路径 / namespace 的兼容历史名称，后续可独立清理。

职责：

- 显示器拓扑与桌面图标原始读取；
- Snapshot / Store / Recovery 诊断；
- 受控 Writer / Recovery / fault-injection 实机验收；
- M3/M4 engineering diagnostics；
- 复制结构化诊断信息。

边界：

- 明确标注“工程诊断工作台”；
- 正常诊断运行不冒充正式产品 UI；
- 正常诊断运行不应成为第二个产品常驻托盘；
- 只有显式 Debug acceptance 配置可以启用旧 M3 resident tray / unattended acceptance surface；
- 所有真实 Writer 行为继续受既有确认、ScopeGate、fresh observation、safety snapshot 与 Recovery 约束。

## 进程与常驻策略

```text
DesktopAgent.WinUI.exe
  = 正式产品
  = 单实例
  = 常驻托盘

DesktopAgent.Diagnostics.exe
  = 工程工作台
  = 正常启动为前台工具
  = 关闭窗口后退出
  = 不建立第二个常驻托盘

Debug acceptance explicitly armed
  = Diagnostics 可临时启用旧 M3 tray / acceptance surface
  = 仅用于受控实机 Gate
```

## 本阶段迁移策略

M5-06 不做 `DesktopAgent.Poc` 目录 / namespace 全量重命名，原因：

1. Tests 当前直接 ProjectReference 该工程；
2. 大量历史 Acceptance / Preview / Editor 类型处于该 namespace；
3. 全量 rename 对本阶段“职责拆分”没有新增安全价值，却显著扩大 diff 与回归面。

本阶段只改：

- AssemblyName / IDE display identity；
- Workbench 视觉身份与安全提示；
- 正常运行的 resident tray 语义；
- README / runbook；
- 后续必要的边界测试。

## Gate

### Automated / Local

- `dotnet build DesktopAgent.sln -c Debug`；
- `dotnet build DesktopAgent.sln -c Release`；
- tests 全绿；
- WinUI 不新增对 `DesktopAgent.Poc` 的 ProjectReference。

### Physical Windows

1. `DesktopAgent.WinUI` 继续单实例、关闭到托盘、托盘恢复与退出正常；
2. `DesktopAgent.Diagnostics` 标题与页面明显显示 Diagnostics Workbench；
3. 正常 Diagnostics 启动不出现第二个 DesktopAgent tray icon；
4. 关闭 Diagnostics 主窗口后进程退出；
5. Diagnostics 只读读取、快照和现有工程入口仍正常；
6. 不进行新的真实 Writer 验收，除非单独进入既有 Debug acceptance Gate。

## 非目标

- 不在 M5-06 删除 WPF；
- 不把 Diagnostics 功能搬入 WinUI；
- 不把 Writer 放进正式产品壳；
- 不做安装包 / 发布通道拆分，留给 M5-08；
- 不做长时间性能验收，留给 M5-07。
