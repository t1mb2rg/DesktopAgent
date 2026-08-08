# M3-01B：连续 Desktop Observation 稳定性

> 状态：实现完成，等待 CI  
> 日期：2026-08-08  
> 范围：M3-A，只读；不修改 M2 Recovery，不接入 Writer

## 1. 目标

在 M3-01A 完整单次 Observation 之上建立“双采样稳定事实”：只有连续两次 fresh capture 都完整成功，并且当前物理事实一致时，才向后续 Diff 提供 StableObservation。

```text
Capture A
→ sample interval
→ Capture B
→ pure stability compare
→ Stable / Unstable / Unavailable
```

Stable 时以后一次 fresh Observation B 作为事实输出。

## 2. 三种状态

### Stable

两次 capture 均成功，并且：

- topology 等价；
- Auto Arrange 相同；
- IconSpacing 相同；
- StableId 集合相同；
- 每个 StableId 的当前图标语义事实相同。

### Unstable

两次 capture 都是完整 Observation，但上述当前事实至少一项发生变化。

典型 issue：

- TopologyChanged；
- AutoArrangeChanged；
- IconSpacingChanged；
- IconAdded；
- IconRemoved；
- IconChanged。

### Unavailable

第一次或第二次 capture 本身无法建立完整事实，例如 incomplete Shell read 或 capture 内 topology mutation。

Unavailable 与 Unstable 分开记录，避免把“没看清”误当成“确认正在变化”。

## 3. 比较语义

稳定性比较忽略纯诊断元数据：

- ObservationId；
- ObservedAtUtc；
- Shell read time；
- Shell connection method；
- capability reader 的 connection method / fallback exception。

这些字段变化不代表桌面物理事实变化。

图标枚举顺序和显示器枚举顺序同样不具有语义。

StableId 与 DeviceName 继续使用 Ordinal 精确匹配。

## 4. 图标稳定事实

对于相同 StableId，比较：

- DisplayName；
- ParsingName；
- ItemKind；
- PixelPosition；
- MonitorDeviceName；
- IsSystemItem；
- IsFileSystemItem。

任一变化都会使两次 Observation 判为 Unstable。

## 5. Application 编排

`StableDesktopObservationSource` 只依赖：

- `IDesktopObservationSource`；
- `DesktopObservationStabilitySettings`；
- `TimeProvider`。

它不读取 history / journal，不依赖 WPF / WindowsInterop，也不获得 Writer。

采样间隔作为 settings 输入，不在 Core 冻结具体毫秒数；后续 M3-02 可根据实机事件节奏调整。

## 6. 与 M2 Recovery 的关系

M3-01B 不重构或复用 `RecoveryObservationStabilityEvaluator`。

M2 Recovery 刚完成真实 crash-window 验收，其稳定性只关注 execution plan 的 relevant icons；M3 稳定性是 whole-desktop observation 语义。当前优先保持两条已验证语义独立，避免为了 DRY 修改 M2 Recovery。

## 7. 测试

覆盖：

- 枚举顺序和诊断元数据变化不影响稳定性；
- topology fact 变化；
- Auto Arrange / IconSpacing 变化；
- Ordinal StableId add/remove；
- 相同 StableId 的图标事实变化；
- 第一次 capture unavailable 后立即停止；
- 第二次 capture unavailable；
- 两次完整且相同 -> Stable，使用第二次 Observation；
- 两次完整但变化 -> Unstable。

## 8. 后续

M3-01C 将只消费 StableObservation，比较 previous stable fact 与 current stable fact，生成 `DesktopStateChangeSet`。

在 M2 真实双显示器 topology acceptance 完成前，后续链路仍不会连接 unattended Writer。
