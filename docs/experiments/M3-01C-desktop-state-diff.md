# M3-01C：Stable Desktop State Diff

> 状态：实现完成，等待 CI  
> 日期：2026-08-08  
> 范围：M3-A，只读状态比较与当前会话 baseline；不生成 Suggestion，不接入 Writer

## 1. 目标

比较 previous stable observation 与 current stable observation，生成确定性的 `DesktopStateChangeSet`，回答：

> 真实桌面从上一份已确认稳定事实到当前稳定事实，发生了什么？

该语义与 M2 Preview Diff 不同：

```text
M2 Preview Diff:
current desktop vs template target
→ 准备如何整理

M3 State Diff:
previous stable fact vs current stable fact
→ 真实桌面刚刚发生了什么
```

## 2. ChangeSet

`DesktopStateChangeSet` 包含：

- PreviousObservationId；
- CurrentObservationId；
- optional `DesktopTopologyChange`；
- optional `DesktopPositioningCapabilitiesChange`；
- deterministic `DesktopItemStateChange[]`。

若以上均为空，则 `HasChanges = false`。

## 3. Structural changes

### Topology

继续使用 `DisplayTopologyComparer`：

- 枚举顺序无语义；
- DeviceName 使用 Ordinal 精确匹配；
- Bounds / WorkArea / DPI / IsPrimary 等任一事实变化都会形成 topology change。

### Positioning capabilities

只比较当前物理能力事实：

- Auto Arrange；
- IconSpacing。

Shell connection method 等诊断元数据不进入 state diff。

## 4. Item changes

StableId 使用 Ordinal 精确集合比较。

每个 item change 为：

- Added；
- Removed；
- Changed。

Changed 使用 flags 精确记录变化事实：

- DisplayName；
- ParsingName；
- ItemKind；
- Position；
- MonitorAssignment；
- SystemItemFlag；
- FileSystemItemFlag。

`IsMoved` 仅由 Position 或 MonitorAssignment 变化决定。

ChangeSet 中 item 始终按 StableId Ordinal 排序，避免 Shell 枚举顺序污染结果。

## 5. 当前会话 baseline

新增 `DesktopStateBaselineTracker`。

```text
first stable observation
→ BaselineEstablished
→ no diff / no startup notification

next stable observation
→ Compare(previous, current)
→ baseline = current
```

baseline 只保存在当前进程内存中。

当前不从 SQLite history 或旧 snapshot 恢复 baseline，原因是：

- history / Store 是历史与意图，不是当前物理事实；
- `DesktopIconDescriptor.StableId` 当前只冻结“当前会话内稳定”；
- 启动后必须先 fresh observe，再建立本次会话的比较基线。

`Reset()` 后下一份 stable observation 重新建立 baseline。

## 6. Topology change 与 item change

Diff 层忠实记录事实，因此 topology change 与由其伴随产生的 item position / monitor changes 可以同时存在。

后续 Suggestion / Notification policy 可以把 topology 视为 structural change，抑制逐图标通知风暴；Diff 层本身不丢失事实。

## 7. 测试

覆盖：

- 枚举顺序和诊断元数据变化得到 empty diff；
- Added / Removed 使用 Ordinal StableId；
- item change 顺序确定；
- Position change；
- MonitorAssignment change；
- metadata flags 精确分类；
- topology + Auto Arrange + IconSpacing structural changes；
- 首份 stable observation 只建立 baseline；
- 后续 stable observation 与上一份 baseline 比较；
- baseline 每次比较后推进；
- Reset 后重新建立 baseline。

## 8. 后续

M3-01D 将把：

```text
WindowsDesktopObservationSource
→ StableDesktopObservationSource
→ DesktopStateBaselineTracker
```

接成单次可操作的 diagnostics / integration harness，在真实 Explorer 上先人工触发并观察 Added / Removed / Moved / structural changes。

真实双屏 M2 topology acceptance 通过前，仍不连接 unattended Writer。
