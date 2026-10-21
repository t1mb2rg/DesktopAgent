# M3-01A：只读桌面 Observation 边界

> 状态：实现完成，等待 CI 与后续实机单次 capture 验收  
> 日期：2026-08-08  
> 范围：M3-A，只读；不接入 unattended Writer

## 1. 目标

在进入 event hint、debounce、stability 和 Diff 之前，先建立一个能够完整描述“当前 Explorer 桌面事实”的只读 Observation 边界。

M3-01A 不监听事件、不判断变化、不生成 Suggestion，也不执行任何真实写入。

冻结链路：

```text
fresh topology A
→ STA capability read + Shell full reread
→ fresh topology B
→ capture 内 topology 一致性检查
→ completeness / identity validation
→ M1 monitor assignment
→ DesktopObservation
```

## 2. Application 边界

新增 `DesktopAgent.Application` 项目。

当前只定义系统能力 port 与应用级结果：

- `IDesktopObservationSource`；
- `DesktopObservationCaptureResult`；
- `DesktopObservationCaptureIssue`。

Application 只引用 Core，不引用 WPF、WindowsInterop、Shell COM 或 SQLite。

## 3. Writer 隔离

原 `IDesktopIconPositionWriter` 同时暴露 `GetCapabilities()` 与 `MoveDesktopIcon()`。

M3-01A 将只读能力拆为：

```text
IDesktopIconPositioningCapabilitiesReader
    GetCapabilities()
```

`IDesktopIconPositionWriter` 继续继承该 reader，以保持 M1/M2 兼容。

`WindowsDesktopObservationSource` 的构造函数只接收 capability reader，不接收 `IDesktopIconPositionWriter` 或 Execution Coordinator。

因此当前 M3-A Observation 路径在类型层面没有 `MoveDesktopIcon()` 入口。

## 4. 成功 Observation

`DesktopObservation` 包含：

- ObservationId；
- ObservedAtUtc；
- 完整 `DisplayDescriptor[]`；
- Auto Arrange / IconSpacing；
- 完整 `DesktopIconDescriptor[]`；
- Shell read connection method；
- Shell read time。

构造时防御性复制集合，并拒绝重复 `DeviceName` 和重复 `StableId`。

## 5. Fail-closed 条件

以下情况返回 `Unavailable`，不建立可供 Stability / Diff 使用的事实：

- topology reader 异常；
- 当前无显示器；
- `DeviceName` 重复；
- topology 在一次 capture 的 Shell 读取前后发生变化；
- capability / Shell read 异常；
- `FailedItemCount > 0`；
- StableId 重复；
- Observation model 构建失败。

## 6. Monitor assignment

M3 不新增第二套图标归属规则。

继续复用 M1 的 `DisplayAssignmentService`：

1. 图标坐标落入某显示器 `Bounds` 时归属该显示器；
2. 否则归属距离最近的显示器。

这保证 M3 Observation 与 M1/M2 的显示器归属语义一致。

## 7. 测试

自动测试覆盖：

- 完整 observation capture；
- Observation source 构造依赖不暴露 Writer；
- incomplete read fail-closed；
- duplicate StableId；
- duplicate DeviceName；
- no displays；
- topology 在 capture 内变化；
- Bounds 外坐标继续使用 M1 最近显示器归属语义。

## 8. 后续

M3-01B 在该边界之上实现连续 Observation 的稳定性判定；M3-01C 实现 previous stable observation 与 current stable observation 的状态 Diff。

在真实双显示器 M2 topology acceptance 完成前，M3-A 继续硬停止于 Suggestion，不进入 unattended Writer。
