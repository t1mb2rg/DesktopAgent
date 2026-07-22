# M1-03B：PoC 保存桌面布局快照与快照列表接入

## 实验目标

在 M1-03A 领域模型和 SQLite 持久化的基础上，将快照保存功能接入 PoC UI，实现手动保存桌面布局快照和查看历史快照列表。

## 实际 UI 功能

- **保存当前快照**：用户点击按钮，将最近一次成功的桌面图标读取结果保存为快照
- **快照列表**：显示所有已保存的快照摘要（创建时间、图标数、目标显示器、连接方式等）
- **持久化**：PoC 重启后仍可查看历史快照
- **安全约束**：读取不完整时禁止保存，显示器拓扑变化后使旧结果失效

## 数据流

```
Shell 只读读取 (M1-02)
  → 显示器归属分配
  → 缓存 DesktopIconReadResult (_lastReadResult)
  → 用户点击"保存当前快照"
  → DesktopLayoutSnapshotFactory.Create(icons, targetMonitor, connectionMethod)
  → SqliteDesktopLayoutSnapshotStore.SaveAsync(snapshot)
  → 快照列表 (ListAsync)
```

## 为什么读取不完整时禁止保存

`DesktopLayoutSnapshot` 当前不保存 `FailedItemCount`。如果有任何图标读取失败（例如 PIDL 绑定失败），已保存的快照将不完整，但无法从快照数据中识别出这一点。后续在此类快照上执行恢复操作可能导致部分图标丢失。

因此规则是：只有在 `FailedItemCount == 0` 时才允许保存。

## 默认数据库路径

```
%LocalAppData%\DesktopAgent\desktop-agent.db
```

由 `DesktopAgentDataPaths.DefaultDatabasePath` 提供。

## 手动测试步骤

1. 启动 PoC
2. 确认显示器拓扑正确加载，目标显示器正确选中
3. 确认状态栏显示"SQLite 已初始化"及数据库路径
4. 点击"读取桌面图标"
5. 确认图标列表正确显示，包含显示器归属
6. 确认"保存当前快照"按钮已启用（无失败项时）
7. 点击"保存当前快照"
8. 确认状态栏显示 SnapshotId、图标数量等信息
9. 确认快照列表中出现新快照
10. 重启 PoC
11. 确认快照列表仍显示之前保存的快照

## 如何检查数据库是否在重启后保留

1. 保存一个快照
2. 关闭 PoC
3. 重新启动 PoC
4. 快照列表应仍显示之前保存的快照
5. 也可直接查看 `%LocalAppData%\DesktopAgent\desktop-agent.db` 文件

## 如何清理测试快照数据库

手动操作（不要在程序中自动删除）：

```powershell
Remove-Item "$env:LocalAppData\DesktopAgent\desktop-agent.db"
```

或者删除整个目录：

```powershell
Remove-Item -Recurse "$env:LocalAppData\DesktopAgent"
```

## M1-04 后续边界

- **不移动图标**：M1-03B 仍然只是只读读取 + 手动保存
- **不恢复布局**：快照仅用于保存，不用于恢复
- **不删除快照**：UI 中不提供删除按钮
- **不自动保存**：仅手动触发
- **不后台常驻**：PoC 关闭后无后台进程
