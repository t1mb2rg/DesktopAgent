# M3-02F：Periodic Fallback Observation Hints

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI
- 范围：M3-A，只读 hint source

## 目的

补上事件驱动监控天然存在的盲区：有些真实桌面变化没有可靠的 Windows event 可供 DesktopAgent 消费。

当前最明确的例子是：

> 用户只拖动一个已有桌面图标的位置。

此时通常不会发生文件系统 Created / Deleted / Renamed，因此 M3-02D `FileSystemWatcher` 不会发 hint。

Periodic fallback 的职责非常窄：

```text
interval elapsed
→ publish PeriodicFallback hint
→ existing M3 hint batch / pump / observation pipeline
```

它本身不读取桌面。

## 数据与边界

新增：

- `PeriodicObservationHintSettings`
- `IPeriodicObservationHintClock`
- `TimeProviderPeriodicObservationHintClock`
- `PeriodicObservationHintSource`

固定 provider：

```text
PeriodicObservationFallback
```

固定 hint kind：

```text
PeriodicFallback
```

每次 tick 只发布：

```text
(kind, provider)
```

不携带任何 desktop fact、路径、图标坐标或历史状态。

## 为什么不是直接轮询 Shell

不把 timer 与 Shell read 绑死：

```text
Timer
→ Hint
→ HintBatcher
→ single-flight
→ Stable Observation
```

这样有几个好处：

1. 如果 tick 刚好撞上 FileSystem / Shell / display hint，可由同一 batch 合并；
2. 完整 Shell observation 仍统一受 single-flight gate 控制；
3. periodic source 不需要知道 WPF / WindowsInterop / Writer；
4. 后续可动态调长 interval，而不改变事实建立逻辑；
5. correctness 不依赖某一个事件 API 是否可靠。

## Tick 语义

`RunAsync()`：

1. 先等待完整 interval；
2. interval 完成后发布一个 PeriodicFallback；
3. 再等待下一 interval。

启动时不会立即发 tick，因为监控 session 本身已经负责 baseline prime。

Interval 必须 `> 0`。

## Callback failure

如果某一 tick 的 hint publish callback 抛出异常：

- 当前 tick 被视为丢失；
- periodic loop 不退出；
- 下一 interval 继续提供 eventual re-observation 机会。

服务 cancellation 不被吞掉，正常向外传播。

## TimeProvider / test clock

生产默认通过 `TimeProvider.System` 执行 delay。

测试使用 `IPeriodicObservationHintClock` 手动完成 tick，不依赖真实 sleep，避免定时测试抖动。

## 自动测试

覆盖：

- zero / negative interval 被拒绝；
- 第一 interval 完成前不发布；
- tick 发布固定 `PeriodicFallback + PeriodicObservationFallback`；
- 每完成一个 interval 发布一次；
- publish callback 单次异常不杀后续 tick；
- delay 期间 cancellation 正常传播且不发布 hint。

## 与 FileSystemWatcher 的关系

两者互补，而不是互相替代：

```text
FileSystemWatcher
  → 低延迟提示新增 / 删除 / 重命名

PeriodicFallback
  → 低频兜底所有可能漏掉的变化
```

周期源不能证明“发生了变化”，它只周期性要求系统重新观察。

真正是否变化仍由：

```text
fresh Observation
→ Stability
→ previous stable baseline vs current stable
→ State Diff
```

决定。

## 下一步 M3-02G

把 periodic source 接入 `M3ReadOnlyMonitoringSession`。

PoC 初始 interval 建议从较保守的值开始，例如 10–15 秒，而不是高频轮询。

后续需要在真实 Windows 桌面测量：

- 单次 full observation 耗时；
- 平均 CPU；
- Shell COM 压力；
- 空闲内存变化；
- 图标数量增长后的成本。

再确定正式默认 interval。

即使未来 Shell-specific hint 足够可靠，periodic fallback 也建议保留，只需把周期拉长，作为最终收敛兜底。
