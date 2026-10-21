# Workspace 系统图标移动

日期：2026-10-20，尚未提交。用户明确要求主屏完全清空，包括回收站等系统图标，不接受自动 Keep/Exclude 留在主屏。

## 根因

`DesktopWorkspaceLocalItemFactProjector.IsMovable` 原来根据系统项目类别统一返回 false；几何 Adapter 也把所有系统图标视为固定障碍。底层 `DesktopShellIconPositionWriter` 则通过枚举桌面、唯一 StableId 匹配 PIDL、SelectAndPositionItems 和 GetItemPosition 回读进行位置移动，不要求文件系统路径。仅修正 IsMovable 会造成移动后固定障碍改变、几何指纹变化，执行仍会中止。

## 修复边界

- Workspace 投影允许具有规范 `::{GUID}` Shell ParsingName、且 StableId 与其精确一致的系统图标参与移动。缺失身份、显示名回退、错误 GUID 或身份不匹配仍不可移动。
- 几何层共用同一规则，可靠身份的可移动系统图标不再误算为固定障碍。没有可靠身份的系统项目仍作为障碍，其位置变化仍影响几何指纹。
- 不修改旧单屏 Direct Apply、旧 LogicalWorkspace 的历史限制；本轮对应新的 Workspace 预览/执行链。
- 不添加 Keep/Exclude，不隐藏图标，不删除文件，不改桌面系统设置。移动仍要求有效实测网格、完整设计、等价检查、最终确认、fresh 核验、先持久化日志及坐标回读。

## 验证

- 修复前身份测试复现回收站/此电脑被统一拒绝。
- 新增 9 项回归：6 个身份场景、三屏回收站迁出主屏并通过精确预览/独立等价检查、模拟动态执行成功、模拟坐标不精确时停止后续写入并保留 NeedsAttention 日志。
- 全量 Release：2565 通过，0 失败，0 跳过。`artifacts/release-baseline/system-icons.trx`。
- PackagingTest Debug 构建通过：`artifacts/ai-workspace-system-icons/DesktopAgent.WinUI.exe`。
- 新构建实机启动成功。Computer Use 的同名多实例窗口画面与辅助功能树不一致，未据此点击真实应用确认；用户这份 94 项资产的完整精确预览未完成实机验收。
- 本轮没有实际移动 Windows 回收站或任何桌面图标；模拟 Writer 测试不代表真实 Shell 移动已验收，原生跨屏移动与恢复仍需实机确认。
