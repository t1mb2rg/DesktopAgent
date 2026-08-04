# M2-03C-C-B：WPF 图标规则编辑与预览接入

## 阶段目标

完成桌面图标规则从持久化层到 WPF 的闭环：
- DI 注册 IDesktopLayoutIconRuleStore
- 模板编辑器规则编辑（加载/编辑/保存/删除）
- 双 Store 保存协调与补偿
- Preview 窗口规则感知预览
- 全部严格只读安全边界

## DI 接线

`App.xaml.cs` 注册：
```
services.AddSingleton<IDesktopLayoutIconRuleStore>(
    new SqliteDesktopLayoutIconRuleStore(DefaultDatabasePath));
```

`MainWindow` 接受 `IDesktopLayoutIconRuleStore`，透传给 Editor 和 Preview 窗口。

## 规则草稿模型

`LayoutIconRuleEditorDraft` — 独立可测试的规则编辑草稿：
- StableId：不 Trim、不转换大小写
- Kind 枚举（Exclude/Pin/ZoneOverride）
- Exclude/Pin 时自动清除 TargetZoneId
- ZoneOverride 时用户必须明确选择 Zone
- `TryBuild` 方法 fail-closed

`LayoutTemplateEditorDraft` 扩展：
- `ObservableCollection<LayoutIconRuleEditorDraft> Rules`
- `TryBuildRuleSet()` 构造 `DesktopLayoutIconRuleSet`
- ZoneOverride 的 TargetZoneId 必须在当前草稿 Zones 中存在

## 编辑器 UI

- TabControl：「模板与分区」|「图标规则」
- 规则列表显示 StableId、Kind、Target Zone
- 操作：「添加规则」「删除选中规则」
- StableId 编辑框不自动 Trim
- Kind 下拉框：Exclude / Pin / ZoneOverride
- Target Zone 下拉框仅在 ZoneOverride 时启用
- 状态统计：总规则数、Exclude/Pin/Override 数

## Template + Rule 加载语义

1. GetAsync(templateId) → 模板
2. RuleStore.GetAsync(templateId) → 规则
3. 模板不存在 → 失败
4. RuleSet 返回 null → 数据不一致，fail-closed
5. RuleSet 返回 Empty(templateId) → 正常空规则列表

## 双 Store 保存协调与补偿策略

`LayoutTemplateAndRulesSaveCoordinator`：

保存前验证：
- TemplateId 一致
- ZoneOverride TargetZoneId 存在于待保存模板

协调流程：
A. 读取保存前状态 (previousTemplate, previousRuleSet)
B. 保存新模板
C. 保存新规则
D-E. 回读验证模板和规则一致

补偿策略：
- 规则保存失败 → 恢复 previousTemplate + previousRuleSet
- 新模板时规则保存失败 → 删除刚创建的模板
- 补偿成功 → FailedAndCompensated
- 补偿失败 → FailedCompensationIncomplete（禁止 MarkSaved）

结构化结果：
- Success / FailedBeforeAnyWrite / FailedAndCompensated
- FailedCompensationIncomplete / VerificationFailedAndCompensated
- VerificationFailedCompensationIncomplete

## 为什么不宣称跨 Store 原子事务

模板 Store 和规则 Store 是两个独立接口，底层是同一个 SQLite 数据库但各自管理事务。
协调器通过补偿策略保证最终一致性，但不伪装为跨 Store 原子事务。

## 规则感知 Preview Core 模型

`DesktopLayoutRuleAwarePreviewPlanner`：
- 输入：template, ruleSet, displays, selectedTargetDeviceName, iconSpacing, icons
- 流程：验证 → 按 Ordinal ASC → 解析目标显示器 → 分离 OOS → ResolveTemplate → ResolveRules → Place → 投影 PreviewItems
- 每个 PreviewItem 包含 Icon、Rule、Disposition、PlacementState、坐标、Zone

Disposition：Excluded / Pinned / ZoneOverride / Default
PlacementState：Fixed / AlreadyAtTarget / MoveRequired / Unassigned

## 空规则兼容性

`DesktopLayoutIconRuleSet.Empty(templateId)` 时，Default 图标的分配 Zone、SlotIndex、TargetPosition 与旧 `DesktopLayoutPreviewPlanner` 的无规则路径逐项一致。

## Scene 映射

- Exclude/Pin → Fixed（不显示目标轨迹）
- Override assigned → AlreadyAtTarget 或 MoveRequired
- Default assigned → AlreadyAtTarget 或 MoveRequired
- Unassigned → 不伪造 TargetPosition
- 摘要显示：Excluded/Pinned/Override/Default 计数

## WPF 生命周期和 generation 保护

- Preview 生成时 fresh re-read 模板和规则（相同 generation）
- 冻结 generation → 异步操作完成后验证 generation
- 取消令牌与窗口关闭关联

## 测试数量与结果

- 新增测试：48 个（草稿 20 + 协调器 6 + Preview 15 + 安全 7）
- 全量：1155/1155 通过（CI build #75）
- Build：0 errors
- NuGet：0 漏洞

## 只读安全边界

- 扫描确认：Editor/Preview 路径无 MoveDesktopIcon 调用
- 扫描确认：无 ExecuteWithScopeGateAsync 调用
- 无 Writer 写操作

## 实机手工验证清单

- [ ] 编译 WPF
- [ ] 启动应用
- [ ] 打开模板编辑器 → 创建/编辑规则
- [ ] 保存模板+规则
- [ ] 重新加载验证 round-trip
- [ ] 删除模板 → 规则级联删除
- [ ] 打开布局预览 → 显示规则感知结果
- [ ] 验证 Exclude/Pin/Override 展示

## 下一阶段建议

- WPF Preview 画布颜色/线型区分不同 Disposition
- 规则编辑器接入 Shell Reader 自动获取 StableId
- System Tray 功能
- 安装程序
