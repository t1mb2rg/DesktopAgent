# M1-04B：PoC 单图标相邻移动与同会话原位恢复

## 阶段目标

在 M1-04A 后端（Writer、Planner、Capabilities）基础上，将单图标相邻移动与同会话原位恢复接入 PoC UI，并通过一次受控正常路径实机验证。

## 安全边界

移动桌面图标属于真实桌面写入。本阶段仅允许移动 `IsFileSystemItem == true && IsSystemItem == false` 的普通文件系统图标，不处理回收站、此电脑、控制面板等 Shell 系统项目。

M1-04B 只恢复本次会话中由 PoC 移动的单个图标，不实现整份布局恢复、批量移动或跨进程恢复。

## 为什么移动前必须重新读取

用户从点击“读取桌面图标”到点击“移动”之间，桌面状态可能发生变化。因此移动流程必须重新读取整个桌面，并重新获取定位能力，不能直接依赖旧的 `_lastReadResult`。

安全快照同样必须使用移动前的 `freshAssignedReadResult` 创建，以反映写入前的真实桌面状态。

## Preparation Service 的验证

纯函数 `DesktopIconMovePreparationService.Prepare` 依次执行：

1. `selectedStableId` null/空白检查；
2. `readResult`、`targetDisplay`、`capabilities` null 检查；
3. `FailedItemCount` 必须为 0；
4. 自动排列必须关闭；
5. StableId 使用 `StringComparison.Ordinal` 精确匹配且恰好出现一次；
6. 必须为文件系统项目、非系统项目；
7. 必须有 `MonitorDeviceName`；
8. 必须属于当前目标显示器；
9. 按唯一匹配索引排除选中图标后构建 `occupiedPositions`；
10. 调用 Planner 规划相邻空位。

### 为什么按索引排除

对象可能通过 `with { }` 或其他映射过程被复制。依赖 `ReferenceEquals` 会把选中图标自身错误地加入占用集合，因此实现保存唯一匹配索引，并只跳过该索引。

## 目标显示器唯一匹配

`UniqueDisplaySelector.FindByDeviceName` 位于 Core 层，使用 `StringComparison.Ordinal` 对 `DeviceName` 做唯一匹配：

- 0 个匹配：在 Writer 调用前拒绝；
- 多于 1 个匹配：在 Writer 调用前拒绝；
- 恰好 1 个匹配：返回该 `DisplayDescriptor`。

该逻辑不引用 WPF、Shell COM、SQLite 或 WindowsInterop，并有纯逻辑自动化测试覆盖正常、零匹配、重复匹配、大小写差异及参数异常。

## 相邻空位规划

复用 M1-04A 的 `DesktopIconMovePlanner.PlanAdjacentMove`，候选顺序为 Right → Left → Down → Up。

## 最终用户确认

移动前弹出默认选择 No 的最终确认框，显示：

- 图标名称；
- 完整 StableId；
- 目标显示器；
- 原位置和目标位置；
- 移动方向；
- 图标间距；
- 安全快照将在移动前保存并回读验证；
- 移动后必须恢复原位置。

只有用户明确选择 Yes 才会继续。

## 安全快照保存和回读验证

移动前使用 `freshAssignedReadResult` 创建完整快照。保存成功后立即调用 `GetAsync` 回读，并验证：

- SnapshotId 一致；
- 图标数量一致；
- 目标 StableId 恰好匹配一次；
- 持久化位置与内存中的移动前位置一致。

快照列表刷新失败也会中止移动，确保 Writer 调用前任一步骤失败时保持零真实写入。

## MoveRecoverySession 创建时机

恢复会话在所有只读准备、安全快照保存、回读验证和列表刷新成功后创建，并且创建发生在 Writer 调用之前。

从恢复会话创建开始，任何 Writer 异常都不能丢弃会话。

## Writer 异常与位置不确定状态

Writer 可能在已调用 `SelectAndPositionItems` 后抛出异常，因此异常并不等价于“图标没有移动”。此时：

- 保留恢复会话；
- 设置 `IsPositionUncertain = true`；
- `LastKnownPosition` 仅表示最后确认的位置，不猜测当前真实位置；
- 保持“恢复原位置”可重试。

Writer 成功返回实际位置后，`IsPositionUncertain` 恢复为 false。

## 恢复会话的清除条件

只有 `result.ActualPosition == session.OriginalPosition` 时才清除会话。以下情况均保留会话：

- `IsExact == false` 且实际位置不等于原位置；
- Writer 异常；
- 恢复调用返回的位置不等于原位置。

缓存更新失败不会改变对桌面实际恢复结果的判断，但会提示用户重新读取桌面图标。

## 关闭窗口保护

有操作进行中时禁止关闭。存在活跃恢复会话时弹出默认 No 的警告；用户仍可明确选择 Yes 强制关闭，但程序不会自动恢复。

## UI 状态管理

`IconRow` 保留完整 StableId、强类型 `PixelX`/`PixelY`、`IsOnTarget` 和原始 `MonitorDeviceName`。截断 StableId、显示器名称和目标标记仅用于展示，不参与安全判断。

所有交互控件的启用状态统一由 `UpdateInteractiveState()` 计算。活跃恢复会话期间，仅恢复按钮和诊断复制保持可用。

顶层 XAML 区域依次为：标题、显示器拓扑、图标标题、图标列表、单图标验证、快照标题、快照列表和诊断状态栏。

## 自动化验证

首次实机验证前完成：

- Release Build：0 Warning，0 Error；
- 自动化测试：131/131 passed；
- NuGet 漏洞检查：未发现易受攻击的软件包；
- GitHub Actions：Build 和 Test 通过。

## 首次受控实机验证结果

验证日期：2026-07-23。

测试对象：临时桌面文件夹 `DesktopAgent-M1-04B-Test`。

验证条件：

- Windows 桌面“自动排列图标”关闭；
- 选中对象被识别为文件系统、非系统图标；
- 对象属于当前目标显示器；
- 使用同一个 PoC 会话完成移动和恢复。

实验数据：

| 项目 | 结果 |
|---|---|
| SafetySnapshotId | `6bd3daea-7466-4d4f-80d7-527c0dcbec91` |
| 原位置 | `(2114,507)` |
| 请求位置 | `(2190,507)` |
| 移动实际位置 | `(2190,507)` |
| 移动方向 | Right |
| 水平位移 | 76 px |
| 移动阶段 IsExact | True |
| 恢复实际位置 | `(2114,507)` |

观察结果：

- 肉眼确认测试文件夹确实发生位移；
- 请求位置与移动实际位置完全一致；
- 恢复实际位置与原位置完全一致；
- 未出现 Writer 异常；
- 未进入位置不确定状态；
- 准确恢复后活跃恢复会话正常清除；
- PoC 在恢复成功后才允许安全关闭。

## 验证结论

M1-04B 已完成一次受控的端到端正常路径验证：

```text
完整读取
→ 能力检查
→ 目标显示器唯一匹配
→ 相邻位置规划
→ 安全快照保存与回读验证
→ 单图标真实移动
→ 实际位置回读
→ 同会话原位恢复
```

本次验证证明单个普通文件系统图标能够按规划位置精确移动，并在同一会话内精确恢复到原位置。

## 尚未验证的范围

当前结论不覆盖：

- 批量图标移动；
- 整份桌面快照恢复；
- 程序重启后的恢复；
- 恢复会话持久化；
- Writer 异常注入路径；
- 自动排列开启状态下的真实写入尝试；
- 所有 Windows 或 Explorer 版本；
- 桌面拓扑在移动与恢复之间发生变化的场景。
