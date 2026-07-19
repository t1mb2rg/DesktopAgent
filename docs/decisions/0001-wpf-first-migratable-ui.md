# ADR-0001：首版使用 WPF，并保持 WinUI 3 可迁移性

- 状态：已接受
- 日期：2026-07-19

## 决策

首版使用 WPF。业务模型、Application、AI、数据库与 Windows 系统互操作不依赖 WPF 类型。后续可以增加独立的 WinUI 3 表现层，而不重写核心能力。

## 原因

DesktopAgent 的高风险能力主要是桌面透明层、图标定位、托盘、Explorer 生命周期和多显示器处理。WinUI 3 无法消除这些 HWND、Shell COM 与 Win32 依赖，而 WPF 在复杂窗口行为方面更成熟。

## 约束

- WPF 类型不得进入 Core、Application 或 AI；
- 所有系统调用集中到 WindowsInterop；
- 设计令牌和页面样式只存在于表现层；
- ViewModel 避免依赖 WPF 专用类型。
