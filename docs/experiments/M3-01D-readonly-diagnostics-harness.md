# M3-01D：只读 Observation Diagnostics Harness

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI 与 Windows 11 / Explorer 人工验收
- 范围：M3-A，只读

## 目的

把 M3-01A / 01B / 01C 已完成的基础能力串成一个可以在真实 Windows 桌面上人工验证的最小闭环：

```text
fresh Observation A
→ sample interval
→ fresh Observation B
→ Stability
→ current-session Baseline
→ State Diff
→ diagnostics text
→ STOP
```

本实验不接入事件监听、不做后台 watcher、不生成 Suggestion、不调用 unattended Writer。

## Application 用例

新增 `DesktopObservationDiagnosticsRunner`。

一次 `RunOnceAsync()`：

1. 调用 `IStableDesktopObservationSource.CaptureStableAsync()`；
2. `Unavailable`：返回失败事实，baseline 不变；
3. `Unstable`：返回稳定性 issue，baseline 不变；
4. `Stable`：把第二次 fresh observation 交给 `DesktopStateBaselineTracker`；
5. 当前进程首次 stable observation 只建立 baseline；
6. 后续 stable observation 与上一份 stable baseline 进行 `DesktopStateDiffer.Compare()`；
7. 比较完成后 baseline 前进到当前 stable observation。

诊断状态显式区分：

- `BaselineEstablished`
- `NoChanges`
- `ChangesDetected`
- `Unstable`
- `Unavailable`

## PoC Diagnostics Window

Debug 模式、且 M2 Acceptance Fault Injection 未启用时，PoC 启动后会同时打开 `M3DiagnosticsWindow`。

窗口只提供：

- `观察一轮`
- `重置基线`
- `复制诊断`

窗口构造函数只接收 `DesktopObservationDiagnosticsRunner`，不接收 `IDesktopIconPositionWriter`。

当前 PoC stability sample interval 通过 DI 配置为 500 ms。该数值不是 Core 常量，后续 watcher/debounce 实验可以单独调整。

## 诊断输出

Stable observation 会输出：

- ObservationId / ObservedAtUtc
- 显示器数量
- 图标数量
- Auto Arrange
- IconSpacing
- Shell read connection method / read time（仅诊断，不参与稳定语义）

ChangeSet 会输出：

- topology change
- Auto Arrange / IconSpacing change
- Added / Removed / Changed item
- Changed item 的 fact flags
- 前后位置与显示器归属

Unstable / Unavailable 会明确输出 issue / capture phase，并注明 baseline unchanged。

## 只读边界

自动测试冻结两层边界：

```text
M3DiagnosticsWindow
    ↓ only
DesktopObservationDiagnosticsRunner
    ↓
IStableDesktopObservationSource + DesktopStateBaselineTracker
```

两者构造函数均不得暴露 `IDesktopIconPositionWriter`。

`DesktopAgent.Application` 项目本身也不引用 WindowsInterop，因此 Runner 无法直接获得 Shell Writer 类型。

## 与 M2 Acceptance Fault Injection 的隔离

M2 HardCrash / SoftError 验收启用时，不自动显示 M3 Diagnostics Window。

原因：

- crash-window acceptance 应保持尽量单一的操作场景；
- SoftError 验收需要观察原有 fail-closed / manual recovery UI；
- M3 诊断窗口不应改变 M2 验收的人机操作路径。

## Windows 人工验收计划

回到 Windows 11 开发机后：

### A. Baseline / no-change

1. Debug 启动 PoC；
2. M3 Diagnostics Window 自动打开；
3. 点击第一次“观察一轮”；
4. 预期 `BaselineEstablished`；
5. 不动桌面，再点击一次；
6. 预期 `NoChanges`。

### B. 单图标移动

1. baseline 已建立；
2. 在 Explorer 桌面手工移动一个普通图标；
3. 等桌面静止；
4. 点击“观察一轮”；
5. 预期 `ChangesDetected`；
6. 对应 StableId 为 `Changed`；
7. `FactChanges` 至少包含 `Position`，跨屏时包含 `MonitorAssignment`。

### C. 新增 / 删除

1. baseline 已建立；
2. 在桌面新建一个临时文件；
3. 观察，预期 `Added`；
4. 删除该临时文件；
5. 再观察，预期 `Removed`。

### D. 操作过程中观察

1. 点击“观察一轮”后，在 500 ms 双采样窗口中移动图标；
2. 若两次完整 observation 不一致，预期 `Unstable`；
3. 再次静止观察时应继续与上一次真正 stable baseline 比较，而不是与 unstable 中间态比较。

### E. Baseline reset

1. 点击“重置基线”；
2. 下一次 stable observation 必须重新返回 `BaselineEstablished`；
3. 不得从 SQLite snapshot / recovery history 恢复旧 baseline。

## 成功标准

- CI build / test 通过；
- Diagnostics Window 无 Writer 依赖；
- 第一次 stable observation 不生成启动假 Diff；
- Unstable / Unavailable 不推进 baseline；
- Stable change 能输出 deterministic State Diff；
- M2 Acceptance Fault Injection 行为不受影响。

## 下一步

M3-01D 通过后，再进入事件提示 / watcher / debounce。

事件只能作为“值得重新观察”的 hint：

```text
Event Hint
→ debounce / quiet window
→ M3-01D pipeline
```

不能让事件 payload 直接成为桌面事实。
