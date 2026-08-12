# M5-07 Performance / Long-run Stability

## 目标

M5-07 不新增产品能力，重点确认 M5 产品壳在真实 Windows 长时间驻留与重复交互下不会出现持续资源增长、托盘失联、Overlay 原生资源泄漏或明显后台 CPU 占用。

本阶段继续保持：

- 不打开 production unattended Writer gate；
- 不执行真实桌面图标移动；
- 不改变 Recovery safety semantics；
- 不修改 M5-01/M5-02 自动化权限矩阵；
- Diagnostics 仅作为工程观测工具，不重新变成第二套常驻产品。

## 风险模型

常驻 DesktopAgent 的主要长期风险不是一次启动失败，而是：

1. Explorer / taskbar 重启后托盘注册丢失；
2. Overlay 反复显示 / 隐藏后 HWND、GDI、USER object 或 managed/native memory 持续增长；
3. close-to-tray / restore / navigation 重复使用后 handle 或 thread 持续增长；
4. settings / pending / layout 刷新造成后台 CPU 周期性抬升；
5. 长时间 idle 后进程仍存活但 UI / tray callback 失去响应；
6. Diagnostics 与正式 WinUI 同时使用时生命周期互相污染。

## Slice A - Tray shell restart resilience

`TrayIconHost` 注册 `TaskbarCreated` 广播。

Explorer / taskbar 重启会清空旧 notification area registration，但 DesktopAgent 进程与主窗口 HWND 仍可继续存在。收到 `TaskbarCreated` 后重新执行 `Shell_NotifyIcon(NIM_ADD)`，恢复同一个 `hWnd/uID` 托盘图标。

约束：

- 不自动打开主窗口；
- 不增加第二个产品实例；
- 不改变 tray menu command；
- 显式退出仍通过 `NIM_DELETE` 清理当前注册；
- re-registration 失败只记录 Debug diagnostics，不让 WndProc 异常逃逸。

## Slice B - Long-run sampler

新增：

```powershell
.\tools\Measure-DesktopAgentLongRun.ps1
```

默认对 `DesktopAgent.WinUI` 采样 30 分钟，每 10 秒记录一次：

- normalized CPU %；
- Working Set；
- Private Memory；
- Handle Count；
- Thread Count；
- GDI Objects；
- USER Objects。

结果写入被 `.gitignore` 排除的：

```text
artifacts/m5-07/long-run-*.csv
```

采样器只读，不操作 UI、不调用 Writer、不重启 Explorer。

## 已完成基线

### 3 minute sampler smoke

真实 Windows 采样 36 个样本 / 约 181 秒：

```text
CPU median:        0.000%
CPU max:           0.058%
Working Set:       182.15 -> 184.61 MiB   (+2.46 MiB)
Private Memory:    113.10 -> 112.73 MiB   (-0.37 MiB)
Handles:           2434 -> 2428            (-6)
Threads:           64 -> 56                (-8)
GDI Objects:       16 -> 16                (0)
USER Objects:      41 -> 41                (0)
```

判定：`PASS`。

Working Set 小幅上升，但 Private Memory 回落，同时 Handle / Thread 净下降，GDI / USER 无增长。当前没有泄漏形状。

### 30 minute idle baseline

真实 Windows 采样 180 个样本 / 约 29.9 分钟：

```text
CPU median:        0.000%
CPU max:           0.020%
Working Set:       188.66 -> 124.50 MiB   (-64.16 MiB)
Private Memory:    110.82 -> 110.14 MiB   (-0.68 MiB)
Handles:           2458 -> 2472            (+14 net)
Threads:           58 -> 58                (0)
GDI Objects:       16 -> 16                (0)
USER Objects:      42 -> 41                (-1)
```

判定：`PASS WITH NOTE`。

CPU 基本静止；Private Memory 稳定并略有回落；Working Set 在后半段被系统明显 trim；Thread / GDI / USER 没有持续增长。

Handle Count 存在值得继续观察的周期性锯齿：前约 20 分钟从 2458 逐步升至 2694，随后一次回落到 2430；之后再次小幅上升并回落，最终为 2472，净变化 +14。当前并不是单调泄漏形状，说明资源存在回收，但 repeated interaction soak 与 2 h resident soak 需要继续确认最低点不会逐轮抬高。

当前基线也说明性能优化的首要目标应是启动 / 常驻 footprint，而不是先追逐一次 GC 或 Working Set 瞬时数字。是否进一步拆分 Resident / Interactive 生命周期，在 M5-07 稳定性 Gate 完成后单独评估。

## 验证阶段

### Gate 0 - Automated freshness

- full solution Debug build PASS；
- full solution tests PASS；
- WinUI product-shell contract tests 锁定 `TaskbarCreated` + `RegisterWindowMessageW`。

### Gate 1 - Sampler smoke

运行 3-5 分钟，确认：

- 正确识别单个 `DesktopAgent.WinUI`；
- CSV 持续写入；
- CPU / memory / handles / GDI / USER 数据有效；
- DesktopAgent 正常使用不受采样器影响。

当前状态：`PASS`。

### Gate 2 - 30 minute idle baseline

启动产品，完成正常 warm-up 后关闭到 tray，保持 30 分钟。

观察重点：

- 进程始终存活；
- tray 始终可用；
- 无持续可见 CPU 活动；
- Private Memory / Working Set 不呈明显单向无限增长；
- Handle / Thread / GDI / USER object 不呈逐样本单调增长。

首轮数据用于建立本项目自己的 baseline，不在采样前凭空冻结不可靠的绝对 MiB 数字。

当前状态：`PASS WITH NOTE (handle sawtooth)`。

### Gate 3 - Repeated interaction soak

在采样期间重复：

- close-to-tray / tray restore；
- tray menu 打开 / 关闭；
- 概览 / 待确认 / 布局 / 设置页面切换；
- Desktop Overlay 显示 / 隐藏；
- Overlay appearance / per-zone color 只做预览与取消，不移动图标。

关注操作停止后的资源是否回到稳定平台，而不是要求每次都回到完全相同的瞬时值。

当前状态：`PENDING`。

### Gate 4 - Explorer restart physical gate

由人工在 Windows 上重启 Explorer / Windows Explorer 后验证：

1. `DesktopAgent.WinUI` 进程仍存活；
2. DesktopAgent tray icon 自动重新出现；
3. 左键恢复与右键菜单仍工作；
4. 不出现重复 tray icon；
5. 产品窗口、settings、layout、Overlay 状态不因 Explorer 重启获得任何新的 Writer authority。

此 Gate 不由自动脚本主动杀 Explorer。

当前状态：`PENDING`。

### Gate 5 - Extended resident soak

在前述 Gate 无异常后执行至少 2 小时 resident soak。允许用户正常使用电脑，不要求专门盯守。

完成条件：

- 无 crash / hang；
- tray callback 仍响应；
- UI 可恢复；
- 资源曲线无持续无界增长趋势；
- 未发现新的 HWND / GDI / USER 泄漏证据。

当前状态：`PENDING`。

## 判定原则

M5-07 的重点是趋势和生命周期稳定性，不以单个瞬时峰值判失败。

以下情况直接视为 finding：

- idle 状态资源长期单向增长且无平台化；
- 每次 Overlay show/hide 都稳定增加 GDI/USER/Handle 且不回收；
- 每次 tray restore 都稳定增加 handle/thread 且不回收；
- Explorer restart 后 tray 不恢复或产生重复图标；
- 长跑期间进程无异常退出但 tray/UI callback 失联；
- 任何性能修复扩大 Writer / Recovery / automation authority。

发现 narrow finding 时继续使用 Directed Repair，再重新跑对应 Gate。
