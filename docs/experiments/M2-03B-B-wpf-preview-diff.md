# M2-03B-B：WPF 真实比例布局预览与差异展示

## 阶段目标

新增独立模态 WPF 窗口，消费 M2-03B-A 的纯 Core PreviewPlan，
按真实 WorkArea 纵横比展示模板、Zone、图标当前位置、目标位置和移动轨迹。

## 窗口架构

- `LayoutPreviewWindow`：独立模态窗口，`ShowDialog()` 打开
- MainWindow 仅提供入口按钮和依赖传递
- PreviewWindow 自己管理模板列表、显示器列表、fresh read、场景构建和 Canvas 渲染
- 窗口级 CancellationTokenSource，关闭时取消

## fresh read 流程

每次点击"生成预览"执行完整 fresh read：

1. Fresh re-read 完整模板（`GetAsync`，不使用缓存 `_loadedTemplate`）
2. 读取显示器拓扑
3. 解析目标显示器（三种绑定模式 UI）
4. STA 线程：`GetCapabilities()` + `ReadDesktopIcons()`
5. 再次读取拓扑检查一致性（双读，按 DeviceName Ordinal 排序比较）
6. `FailedItemCount > 0` → fail-closed
7. `DisplayAssignmentService.AssignMonitor` 分配 MonitorDeviceName
8. `DesktopLayoutSnapshotFactory.Create` 创建临时快照（按 Ordinal ASC）
9. `DesktopLayoutPreviewPlanner.Plan`
10. 构建 Scene → 渲染

## 三种绑定模式

- **SelectedTarget**：`DisplayCombo` 启用，用户选择精确 DeviceName 传给 Planner
- **PrimaryDisplay**：Combo 禁用，显示"解析到主显示器"，传 null
- **ExactDeviceName**：Combo 禁用，显示绑定名称，传 null

DeviceName 使用 Ordinal，不 Trim、不大小写转换、不回退第一块屏幕。

## 模板 fresh re-read

每次生成预览都 `GetAsync(selectedTemplateId)`，使用局部不可变 `template`。
模板加载、刷新、Generate 间通过 `_generation` 防竞态。
过期异步结果检查 generation 后不得回写。

## 异步生命周期

- 窗口级 `CancellationTokenSource`
- 所有 store API 传 CancellationToken
- 模板变化、目标显示器变化、刷新、失效、关闭均增加 generation
- 在写入 `_previewPlan`、`_scene`、任何控件前检查 generation 和 `_isClosing`
- 关闭窗口先标记 `_isClosing`、取消 token

## CanvasTransform 等比例映射

`scale = min(availW / wa.Width, availH / wa.Height)` — 单一 scale。
letterbox 公式：`contentW = wa.Width * scale`，`offsetX = padding + (availW - contentW) / 2`。
支持 16:9/16:10/4:3/竖屏/负坐标/非零 Left/Top。

## Scene 不变量

- `Transform.WorkArea` 必须与 `Plan.TargetDisplay.WorkArea` 完全一致
- Zones 按 SortOrder ASC
- Icons/ItemRows 按 Ordinal ASC
- OutOfScopeRows 按 Ordinal ASC
- 所有映射结果必须为有限数值
- Unassigned 使用 `null` 而非 `double.NaN` 表示无目标
- 所有公开集合防御性只读

## resize 只重绘

`Canvas_SizeChanged` 仅重建 Transform + Scene + RenderScene，
不重新读取桌面、模板或运行 Planner。

## Preview 失效规则

以下操作清除旧预览：模板变化、目标显示器变化、刷新、生成失败、拓扑不一致、模板读取失败。
清除包括 `_previewPlan = null`、`_scene = null`、Canvas 清空、表格清空。

## 自动排列警告

`IsAutoArrangeEnabled` 时显示醒目警告但不阻止只读预览。

## 安全断言

- PreviewPlan 仅用于展示，不是写入授权
- 本窗口无执行按钮
- 不保存安全快照，不创建恢复会话
- 唯一允许的定位接口调用是只读 `GetCapabilities`
- 本窗口永远不能调用 `MoveDesktopIcon`

## 人工 GUI 验收清单

1. 主窗口出现"布局预览"按钮
2. 模态窗口打开，模板列表显示模板名称（非类型名）
3. SelectedTarget：选择显示器后生成成功
4. PrimaryDisplay：自动解析且 Combo 禁用
5. ExactDeviceName：显示绑定名称且 Combo 禁用
6. 点击生成后 fresh re-read 完整模板和实时桌面
7. 16:9 显示器 Canvas 为 16:9，不拉伸
8. 正方形窗口中 Canvas letterbox
9. 调整窗口大小 Canvas 仍保持真实比例
10. Zone 与模板一致，含名称和容量
11. 已就位图标：蓝色圆点标记
12. 需要移动图标：蓝色圆点 + 红色方块 + 绿色连线
13. Unassigned 在表格中以"未分配"显示，Canvas 不绘制目标点
14. OutOfScope 在独立表格中显示
15. 汇总含纵横比、cap/icon 双 ConnectionMethod、AutoArrange 警告
16. 切换模板清除旧预览
17. 切换目标显示器清除旧预览
18. 读取失败不会保留旧预览
19. busy 时禁用所有交互按钮
20. 关闭窗口后桌面图标完全没有变化

## M2-03C 扩展

M2-03C 可在 `DesktopLayoutPreviewPlanner.Plan` 前插入图标过滤/分类层。
