# M1-04B：PoC 单图标相邻移动与同会话原位恢复

## 阶段目标

在 M1-04A 后端（Writer、Planner、Capabilities）基础上，将单图标相邻移动与同会话原位恢复接入 PoC UI。

## 为什么仍分为「代码审核」和「实机执行」

移动操作是破坏性的。本轮提交仅包含所有代码和纯逻辑测试，**本文档撰写时尚未执行任何真实桌面图标移动**。实机验证将在后续手动执行。

## 只允许文件系统、非系统图标

回收站、此电脑、控制面板等 Shell 命名空间项目的行为与普通文件不同，移动后可能无法通过 `SelectAndPositionItems` 正确还原。因此只允许 `IsFileSystemItem == true && IsSystemItem == false` 的图标。

## 为什么移动前必须重新读取

用户从点击「读取桌面图标」到点击「移动」之间，桌面状态可能已发生变化（图标被添加、删除、移动）。因此移动流程必须重新读取整个桌面，使用最新的图标位置和自动排列状态。

## 为什么旧 _lastReadResult 不能直接用于安全快照

`_lastReadResult` 是用户最近一次手动读取的结果，可能已过时。安全快照必须反映移动前**此刻**的真实桌面状态。

## Preparation Service 的验证

纯函数 `DesktopIconMovePreparationService.Prepare`：
1. selectedStableId null/空白检查
2. readResult/targetDisplay/capabilities null 检查
3. FailedItemCount 必须为 0
4. 自动排列必须关闭
5. StableId 精确匹配（恰好 1 个），记录匹配索引
6. 必须为文件系统项目、非系统项目
7. 必须有 MonitorDeviceName
8. 必须属于当前目标显示器
9. **按唯一匹配索引**排除选中图标后构建 occupiedPositions（不使用 ReferenceEquals）
10. 调用 Planner 规划相邻空位

### 为什么按索引排除而非按对象引用

如果未来对象被复制或重建（例如 `with { }` 或 `record` 值复制），`ReferenceEquals` 会失败，导致选中图标也进入 occupiedPositions，使 Planner 错误地将自身位置视为占用。按索引排除是唯一正确的做法。

## Preparation Service 按索引排除

使用 `for` 循环遍历 `readResult.Icons`，匹配 `selectedStableId` 时记录 `selectedIndex`。构建 `occupiedPositions` 时 `index == selectedIndex` 跳过。

## 自动排列检查

移动流程重新调用 `GetCapabilities()`，而非依赖旧的用户手动读取结果。

## 相邻空位规划

复用 M1-04A 的 `DesktopIconMovePlanner.PlanAdjacentMove`，候选顺序 Right → Left → Down → Up。

## 最终用户确认

移动前弹出 `MessageBox`，显示图标名、StableId、原位置、目标位置、方向、间距、安全快照提示。用户必须选择 Yes 才能继续。

## 安全快照保存和回读验证

移动前使用 `freshAssignedReadResult` 创建完整快照。保存成功后立即调用 `GetAsync` 回读验证：SnapshotId 一致、图标数量一致、目标 StableId 恰好匹配一次、持久化位置与内存一致。

## 为什么快照列表刷新失败也中止移动

保守策略：任何移动前异常都应保持「零真实写入」。移动前所有步骤（能力检查、准备、确认、保存、回读）任一失败都应终止，确保桌面不被修改。

## MoveRecoverySession 创建时机

**所有只读准备、安全快照保存和验证成功后，调用 Writer 之前**创建会话。从这一行开始，任何异常都不能清除会话。

## 为什么 Writer 异常后仍保留恢复会话

Writer 内部在 `SelectAndPositionItems` 调用前后已经历完整枚举和匹配过程，且 `SelectAndPositionItems` 已被调用。此时桌面状态不确定。丢弃会话意味着丢失恢复所需信息。

## 恢复会话的清除条件

只有 `result.ActualPosition == session.OriginalPosition` 时才清除会话。任何其他情况（包括 IsExact=false、Writer 异常、ActualPosition 不等于 OriginalPosition）都保留会话用于重试。

## 关闭窗口保护

有活跃会话时弹出警告，默认 No（取消关闭）。用户可选择 Yes 强制关闭，但不会自动恢复。

## 本阶段不做整份布局恢复

M1-04B 仅限于同一会话内恢复最初移动的那一个图标。不通过快照恢复整份布局。

## IconRow 保留强类型状态

`IconRow` 保留 `int PixelX` / `int PixelY`（非字符串），`bool IsOnTarget`，`string? MonitorDeviceName`。展示文本 `StableIdTruncated`、`MonitorDisplay`、`TargetScreenMarker` 仅为 UI 计算属性，不参与安全判断。

## 所有交互控件状态由 UpdateInteractiveState 集中管理

事件处理器只能修改状态字段（`_iconReadInProgress` 等）并调用 `UpdateInteractiveState()`。不得在事件处理器中直接设置 `RefreshDisplaysButton.IsEnabled`、`DisplaysGrid.IsEnabled`、`ReadIconsButton.IsEnabled`、`IconsGrid.IsEnabled`、`SaveSnapshotButton.IsEnabled`、`RefreshSnapshotsButton.IsEnabled`、`MoveConsentCheckBox.IsEnabled`、`MoveSelectedIconButton.IsEnabled`、`RestoreOriginalPositionButton.IsEnabled`。

## 当前提交没有运行 PoC、没有真实移动

本文档撰写时所有代码仅通过自动化测试验证。

## 后续实机测试对象建议

- 临时创建的空文件夹或文本文件
- 不使用回收站、此电脑等系统图标
- 不使用重要文件作为首测对象

## 后续手动验证步骤（尚未执行）

1. 启动 PoC
2. 读取显示器拓扑，确认目标显示器
3. 读取桌面图标，确认目标图标出现在列表中
4. 勾选移动确认框
5. 选中一个文件系统图标
6. 点击「移动到相邻空位」
7. 在确认框中选择 Yes
8. 确认图标已移动到相邻空位
9. 点击「恢复原位置」
10. 确认图标已回到原位置
11. 确认「恢复原位置」按钮已禁用
