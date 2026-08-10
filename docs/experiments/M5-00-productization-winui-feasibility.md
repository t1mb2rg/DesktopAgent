# M5-00 Productization + WinUI 3 Feasibility Gate

## 目标

M5-00 不做正式产品视觉设计，也不新增 Intelligence / Writer 能力。

本阶段只回答一个问题：

> WinUI 3 能否作为 DesktopAgent v1.0 的 Product UI shell，同时保持 M0-M4 的冻结边界、后台生命周期和后续自动化/布局编辑需求？

## Frozen upstream

以下能力在 M5-00 视为上游冻结基线，不因 UI 技术选择改变语义：

- M0-M3 observation / planner / Writer / Recovery / Coordinator / cooldown / fuse
- M4 Intelligence Core 与 `v1.0 CORE FEATURE FREEZE`
- AI 不获得 WriterCommand、physical coordinates、FileMove、DeleteFile
- production unattended gate 继续关闭

## Product UI 与 Developer Workbench

- `DesktopAgent.WinUI`：未来产品 UI 候选壳
- `DesktopAgent.Poc` WPF：继续作为 Developer / Diagnostics Workbench

M5-00 不迁移 WPF 调试能力，也不删除 PoC。

## Feasibility candidate

当前候选配置：

```text
Target:             net10.0-windows10.0.19041.0
Architecture:       x64
Windows App SDK:    2.3.1 Stable
Packaging mode:     unpackaged feasibility shell
WindowsPackageType: None
```

`unpackaged` 是 M5-00 的产品壳验证方向，不等于 M5-08 已经冻结最终 installer / runtime deployment 方案。

## Lifecycle contract

### Single instance

`Program.Main` 在创建 XAML Application 之前通过 `AppInstance.FindOrRegisterForKey` 判断实例所有权。

- 第一实例：继续初始化 WinUI Application
- 第二实例：把 activation redirect 给第一实例并退出
- 第一实例收到 redirect：恢复/激活 Product window

### Close-to-tray

系统关闭按钮不是进程退出命令：

```text
Window close request
→ AppWindow.Closing
→ Cancel=true
→ AppWindow.Hide()
→ process remains alive
```

### Notification area

M5-00 使用 Win32 `Shell_NotifyIcon` 建立最小 tray feasibility host。

- tray icon 与 WinUI 主窗口 HWND 绑定
- 左键/双击/右键在 feasibility shell 中只负责恢复窗口
- 正式 context menu（暂停、待确认数量、打开、退出）属于 M5-05

### Explicit exit

只有显式 Exit：

```text
mark exit requested
→ delete tray icon
→ restore original WndProc
→ unsubscribe Closing
→ Window.Close()
→ process exits
```

## Product architecture constraints carried into M5

M5-00 的 WinUI 壳必须能承载后续已冻结的产品方向，但本阶段不实现它们：

### M5-01 Automation Policy

```text
Manual
Assisted (default)
Automatic

AUTO / QUEUE / ASK / BLOCK
```

Automatic 仍表示“用户授权过的策略自动执行”，不是 AI 获得执行权。

### M5-02 Pending Queue

AI-only 新判断进入 Queue，避免逐项弹确认；Confirm & Remember 可形成 Rule / Confirmed Preference。

### M5-05 Layout Management

后期允许用户重新编辑桌面布局：

```text
enter Layout Edit
→ BLOCK automation for the edited template
→ edit LayoutDraft
→ impact preview
→ atomic commit
→ fresh observation
→ fresh replan
→ resume automation
```

Zone move/resize 保持 ZoneId 时不应破坏 Category / Rule / Preference 语义；Zone 删除需要 impact preview 和 remap/clear 处理。

## CI Gate

必须满足：

- solution restore PASS
- WinUI x64 Debug build PASS
- WinUI x64 Release build PASS
- existing full regression PASS
- M0-M4 test baseline 不退化

CI 只能证明 build/static integration，不能替代真实桌面生命周期验收。

## Physical Windows Gate

在真实 Windows x64 客户端执行：

1. 启动 `DesktopAgent.WinUI.exe`，主窗口出现。
2. 再启动同一 exe：不出现第二个独立窗口/进程；现有窗口被恢复/激活。
3. 点击“隐藏到托盘”：主窗口消失，进程仍存在，tray icon 存在。
4. 点击 tray icon：原窗口恢复。
5. 点击窗口 `×`：窗口隐藏而不是退出；tray 和进程仍存在。
6. 再次点击 tray icon：窗口仍可恢复。
7. 点击“显式退出”：tray icon 被移除，进程结束。
8. 全过程不应触发真实桌面 Writer，不应打开 production unattended automation。

## Baseline resource observation

M5-00 只记录 Product shell 的初始：

- Working Set / Private Bytes
- thread count
- handle count
- startup subjective latency

本阶段不据此做大规模性能优化；正式 profiling 与 1h/8h/24h drift 属于 M5-07。

## Gate outcome

只有 CI + Physical Windows Gate 都通过后，才允许记录：

```text
WinUI 3 Product UI feasibility: PASS
```

在此之前状态均为 `CANDIDATE`。
