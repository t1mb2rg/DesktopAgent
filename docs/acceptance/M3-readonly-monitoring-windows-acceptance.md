# M3-A 只读监控 Windows 实机验收清单

- 日期：2026-08-08
- 目标分支基线：`agent/m3-02g-periodic-monitoring-session`
- 本文档分支：`docs/m3-readonly-monitoring-acceptance`
- 平台：Windows 11 / Explorer / x64 / .NET 10
- 当前安全边界：**只读监控，不解除 unattended Writer gate**

## 1. 为什么要做这轮验收

M3-A 目前已经由自动测试覆盖：

```text
fresh Observation
→ repeated-observation Stability
→ in-memory stable baseline
→ State Diff
→ event hint batching
→ single-flight processing
→ background pump
→ Desktop FileSystem hints
→ periodic fallback
→ diagnostics
```

但以下事实只有真实 Windows / Explorer 能证明：

- Shell COM 在真实桌面上的完整读取行为；
- FileSystemWatcher 对真实 Desktop / Public Desktop 的通知；
- Explorer 图标拖动后物理坐标的实际变化；
- 双显示器 MonitorAssignment 与 topology；
- 15 秒 periodic fallback 的真实发现延迟和成本；
- WPF 窗口、后台 pump、关闭/重启的真实生命周期；
- Debug SoftError 在真实 Writer side effect 后能否不杀进程并安全回主页。

因此在进入 Suggestion / notification / 自动授权之前，先把“眼睛和神经”验完。

---

# 2. 验收前准备

## 2.1 使用专门测试图标

建议在桌面准备几个可随时删除的普通文件：

```text
M3-A.txt
M3-B.txt
M3-C.txt
```

不要拿重要快捷方式、系统图标或工作文件做 Writer / crash fault 测试。

## 2.2 正常 M3 验收时关闭 Acceptance Fault Injection

PowerShell 可先清掉相关环境变量：

```powershell
Remove-Item Env:DESKTOPAGENT_ACCEPTANCE_FAULT_MODE -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_ACCEPTANCE_FAULT_BEHAVIOR -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_ACCEPTANCE_FAULT_CALL -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_ACCEPTANCE_FAULT_STABLE_ID -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_ACCEPTANCE_FAULT_ARM -ErrorAction SilentlyContinue
```

Acceptance Fault Injection 与 M3 diagnostics 是两套不同验收场景，不要同时进行。

## 2.3 拉取并构建

```powershell
cd G:\work\LAB\code\DesktopAgent

git fetch origin
git switch docs/m3-readonly-monitoring-acceptance
git pull --ff-only

dotnet restore DesktopAgent.sln
dotnet build DesktopAgent.sln -c Debug --no-restore
dotnet test tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Debug --no-build
```

预期：

- restore 成功；
- build 成功；
- tests 全绿；
- 不应因为 M3-A 引入新的编译错误。

## 2.4 启动 PoC

```powershell
dotnet run --project src\DesktopAgent.Poc\DesktopAgent.Poc.csproj -c Debug
```

预期：

- MainWindow 正常出现；
- `M3 Read-only Diagnostics` 窗口自动出现；
- 没有 Acceptance Fault Injection 警告框；
- 后台监听初始状态为“未启动”；
- 桌面图标没有被自动移动。

---

# 3. A 组：手动 Observation / Stability / Baseline / Diff

## A1. 第一次观察只建立 baseline

操作：

1. 保持桌面静止；
2. 点击 `观察一轮`；
3. 不进行任何桌面操作，等待结果。

预期：

```text
Status: BaselineEstablished
```

并且：

- 有有效 ObservationId；
- Displays 数量符合实际；
- Icons 数量合理；
- AutoArrange / IconSpacing 有值；
- 没有 ItemChanges；
- 不出现“所有图标都是 Added”的假启动 Diff。

## A2. 无变化重复观察

操作：

1. 不动桌面；
2. 再点击一次 `观察一轮`。

预期：

```text
Status: NoChanges
Diff: empty
```

## A3. 手工移动一个图标

操作：

1. 把 `M3-A.txt` 从当前位置拖到同一显示器另一个明显位置；
2. 等 Explorer 安静；
3. 点击 `观察一轮`。

预期：

- `Status: ChangesDetected`；
- 对应 StableId 出现 `Changed`；
- `FactChanges` 至少包含 `Position`；
- Previous / Current 坐标不同；
- 同屏移动时 MonitorAssignment 通常不变。

## A4. Unstable 不推进 baseline

操作：

1. 点击 `观察一轮` 后，在约 500 ms 的双采样窗口内持续拖动测试图标；
2. 尽量让两次完整 Observation 看见不同位置。

可能结果：

```text
Status: Unstable
```

若命中 Unstable，必须满足：

- diagnostics 显示 Stability issues；
- baseline unchanged；
- 下一次桌面静止后再观察，仍应与上一次真正 stable baseline 比较。

注意：该场景有时序性，没有命中 Unstable 不算失败，可重复几次。

## A5. Reset baseline

操作：

1. 点击 `重置基线`；
2. 再点击 `观察一轮`。

预期：

- Reset 后下一次 stable observation 再次得到 `BaselineEstablished`；
- 不使用 SQLite history 冒充当前 baseline。

---

# 4. B 组：FileSystemWatcher 自动触发

## B1. 启动后台监听

操作：

1. 点击 `启动后台监听`；
2. 不做桌面变化，观察启动日志。

正常完整模式预期：

```text
Status: Started
FileSystemSource.IsRunning: true
FileSystemSource.ActiveWatcherCount >= 1
BaselinePrime: BaselineEstablished / NoChanges
```

同时：

- `观察一轮` disabled；
- `重置基线` disabled；
- `停止后台监听` enabled。

若显示 `StartedDegraded`：

- 记录 FileSystem source issues；
- 监控仍应运行；
- periodic fallback 仍应工作；
- 这不等于 Observation 失败。

## B2. 新建桌面文件

操作：

1. 后台监听保持运行；
2. 在桌面新建 `M3-monitor-created.txt`；
3. **不要点击任何 DesktopAgent 按钮**；
4. 等待约 700 ms quiet window + observation 时间。

预期自动出现 background trigger：

```text
HintKinds: FileSystem=...
HintSources: DesktopFileSystemWatcher
```

随后 fresh State Diff 应包含与新增项目一致的变化，通常为 `Added`。

关键判定：Diff 必须来自 fresh Observation，而不是直接复制 FileSystemWatcher 文件名。

## B3. 重命名

操作：

1. 将测试文件重命名；
2. 等待自动 background trigger。

预期：

- 有 FileSystem hint；
- State Diff 由 fresh StableId 语义决定；
- 不强制要求一定表现为 `Changed`，如果 StableId 因路径改变而变化，`Removed + Added` 也是合理结果；
- 绝不能因为 watcher 提供了 rename payload 就绕过 Observation 直接制造 Diff。

## B4. 删除

操作：

1. 删除测试文件；
2. 等待自动 background trigger。

预期：

- FileSystem hint；
- fresh observation 后出现对应 `Removed` 或符合 StableId 事实的变化。

---

# 5. C 组：Periodic fallback 与纯位置移动

当前 PoC 参数：

```text
Periodic interval = 15 s
Hint quiet period = 700 ms
```

因此静默变化的目标发现延迟约为：

```text
0 ~ 15 s + quiet/stability/observation cost
```

## C1. 只拖动图标，不改文件系统

操作：

1. 后台监听运行；
2. 选择一个已有 `M3-A.txt`；
3. 只拖到另一个位置；
4. 不新建、不删除、不重命名任何文件；
5. 不点击 DesktopAgent 按钮；
6. 等待最多约 17 秒。

预期：

- 不要求 FileSystemWatcher 立即触发；
- 最终自动出现 background trigger；
- HintKinds 包含 `PeriodicFallback`；
- HintSources 包含 `PeriodicObservationFallback`；
- State Diff 出现该图标 `Changed + Position`。

这一条是 M3-02G 最关键的真实验收之一。

## C2. 空闲桌面 1 分钟

操作：

1. 后台监听运行；
2. 完全不操作桌面至少 60 秒；
3. 记录每次 periodic-triggered diagnostics。

预期：

- 大约每 15 秒有 periodic hint；
- 大多数结果应为 `NoChanges`；
- 不应出现并发运行；
- 不应出现不断增加的重复 ItemChanges；
- 不应出现窗口卡死或后台任务失控。

记录：

```text
每轮 observation duration:
CPU 大致范围:
DesktopAgent 内存起始:
DesktopAgent 内存 1 分钟后:
Shell / Explorer 是否出现明显卡顿:
```

本阶段不先写死性能阈值，用实测数据决定正式 interval。

## C3. FileSystem + Periodic 合批（可选）

操作：

1. 观察 periodic 大致节奏；
2. 在下一次 periodic tick 前后制造一次桌面文件新增/删除；

若二者落入同一 batch window，预期：

```text
HintKinds:
  FileSystem=...
  PeriodicFallback=...
```

且只执行一轮 background observation。

该场景有时序性，没有每次命中不算失败。

---

# 6. D 组：Stop / Restart / 生命周期

## D1. Stop 后不再自动运行

操作：

1. 点击 `停止后台监听`；
2. 新建/删除一个测试文件；
3. 等待超过 17 秒。

预期：

- 不再出现 background trigger；
- FileSystem watcher 已停止；
- periodic loop 已取消；
- 手动 `观察一轮` / `重置基线` 恢复可用。

## D2. Restart

操作：

1. 再次点击 `启动后台监听`；
2. 不改变桌面。

预期：

- 创建新的 watcher / pump / periodic loop；
- session baseline 默认保留；
- baseline prime 通常为 `NoChanges`；
- 不出现“双倍 periodic tick”或重复 watcher 触发。

## D3. 关闭 Diagnostics Window

操作：

1. 后台监听运行时直接关闭 Diagnostics Window；
2. 观察应用是否仍正常；
3. 再退出整个 PoC。

预期：

- Window close 会 StopAsync；
- Host.Dispose 最终兜底；
- 进程能够正常退出；
- 不留下 DesktopAgent 后台进程；
- 不出现退出时 ObjectDisposed / cancellation 异常弹窗。

---

# 7. E 组：双显示器真实验收

这是当前 M2 → M3 仍然最重要的真实环境门之一。

## E1. 初始双屏 topology

用户当前主力机为：

- 27 英寸 2K 主屏；
- 左侧 24 英寸竖屏。

操作：

1. 两块屏幕正常启用；
2. 手动 `观察一轮` 建 baseline；
3. 查看 Displays 与图标 MonitorAssignment。

预期：

- Displays = 2；
- DeviceName 唯一；
- Bounds / WorkArea / DPI 与 Windows 当前布局一致；
- 两屏上的测试图标归属正确。

## E2. 跨屏移动图标

操作：

1. 后台监听运行；
2. 将 `M3-A.txt` 从一块屏拖到另一块屏；
3. 等 periodic fallback 自动发现，或停止监控后手动观察。

预期 Diff：

- `Position` changed；
- `MonitorAssignment` changed；
- Current monitor 与实际目标显示器一致。

## E3. Topology 变化

建议优先使用 Windows 显示设置临时禁用/启用副屏，而不是物理拔线。

操作：

1. 建立 stable baseline；
2. 临时禁用副屏；
3. 等系统/Explorer 收敛；
4. 观察 diagnostics；
5. 再恢复副屏。

允许过渡阶段出现：

```text
Unstable
Unavailable
TopologyChangedDuringCapture
```

这些不是错误，只要 baseline 不推进。

最终稳定后必须能建立新的 stable observation，并正确记录 topology change / monitor re-assignment。

---

# 8. F 组：Debug SoftError 不杀进程

这一组与正常 M3 监控分开进行。

## F1. 目的

验证：

```text
真实 Exact Writer 已执行
→ checkpoint 前命中 SoftError
→ controlled exception
→ 现有执行链 fail-closed
→ 错误提示
→ 返回主页
→ DesktopAgent 进程仍存活
```

HardCrash 模式仍必须真正杀进程，不要把两者混用。

## F2. 安全准备

- 只使用专门测试文件；
- 记录其原始位置；
- 不在重要桌面布局上做；
- 先确认当前测试场景的 StableId 和目标 Writer call index。

SoftError ARM：

```text
DESKTOPAGENT_ACCEPTANCE_FAULT_MODE=AfterExactWriteBeforeCheckpoint
DESKTOPAGENT_ACCEPTANCE_FAULT_BEHAVIOR=SoftError
DESKTOPAGENT_ACCEPTANCE_FAULT_CALL=<call index>
DESKTOPAGENT_ACCEPTANCE_FAULT_STABLE_ID=<exact StableId>
DESKTOPAGENT_ACCEPTANCE_FAULT_ARM=I_UNDERSTAND_THIS_MAY_REQUIRE_RECOVERY
```

## F3. 预期

命中后：

- DesktopAgent 不退出；
- 出现 Soft Debug Fault 提示；
- 当前执行窗口关闭/返回 MainWindow；
- 如果真实写入后的状态不能证明安全，人工恢复锁保持；
- 不允许因为“UI 回主页了”就重新开放真实写入。

然后关闭 fault injection 环境变量，再回到普通 M3 验收。

---

# 9. 自动测试已经证明什么，实机还没证明什么

## CI / unit tests 已证明

- Observation model 唯一性规则；
- topology capture 前后夹读；
- incomplete read fail-closed；
- Stability 纯逻辑；
- State Diff 纯逻辑；
- baseline 只接受 stable observation；
- hint quiet/max-delay；
- single-flight；
- pump wake/timer race；
- processing 期间 hint retention；
- FileSystem source 生命周期与 payload 隔离；
- periodic tick / cancellation；
- FileSystem unavailable → `StartedDegraded` periodic-only；
- periodic fault → whole session fail-closed；
- read-only composition 不依赖 Writer。

## 必须实机证明

- Explorer Shell COM full read 真实表现；
- 真实 StableId 在新增/删除/rename/拖动时的行为；
- 真实 FileSystemWatcher Desktop / Public Desktop 通知；
- 纯位置拖动能被 periodic eventual observation 捕获；
- 双显示器真实 MonitorAssignment；
- topology 变化时过渡状态与最终收敛；
- WPF / Host 真实关闭生命周期；
- 15 秒周期的 CPU / 内存 / Shell 压力；
- SoftError 真 side-effect 后不杀进程且保持安全锁语义。

---

# 10. 进入下一阶段前的 Gate

以下项目未满足前，不进入 unattended Writer：

- [ ] 当前 stacked PR 最新 CI 全绿
- [ ] A1/A2/A3 手动 Observation + Diff 通过
- [ ] B2/B3/B4 FileSystem 自动触发通过
- [ ] C1 pure position periodic fallback 通过
- [ ] D1/D2 Stop / Restart 无幽灵任务
- [ ] E1/E2 双屏 MonitorAssignment 通过
- [ ] E3 topology 变化最终收敛通过
- [ ] F3 SoftError 不杀进程通过
- [ ] 空闲 1 分钟没有明显资源异常
- [ ] 没有发现 event payload 绕过 fresh Observation 的路径
- [ ] 没有发现手动 Runner 绕过 single-flight 的路径

全部通过后，下一阶段才讨论：

```text
State Diff
→ Suggestion policy
→ Notification
→ explicit user authorization
→ M2 safe execution pipeline
```

而不是：

```text
Event
→ Writer
```

后者继续明确禁止。

---

# 11. 建议的验收记录模板

每组测试结束后记录：

```text
日期/时间：
分支/commit：
Windows 版本：
显示器拓扑：
测试项：
结果：PASS / FAIL / INCONCLUSIVE
Diagnostics 摘要：
ObservationId：
HintKinds / HintSources：
关键 Diff：
异常/截图：
是否需要代码修复：
```

如果失败，优先保留完整 Diagnostics 文本，不要先根据 UI 现象猜历史发生了什么。

我们继续遵守同一个原则：

> 先保存事实，再分析原因；先重新观察当前状态，再决定下一步。
