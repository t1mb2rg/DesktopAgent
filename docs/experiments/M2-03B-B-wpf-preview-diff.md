# M2-03B-B：WPF 真实比例布局预览与差异展示

## 阶段目标

新增独立模态 WPF 窗口，消费 M2-03B-A 的纯 Core PreviewPlan，
按真实 WorkArea 纵横比展示模板、Zone、图标当前位置、目标位置和移动轨迹。

## 窗口架构

- `LayoutPreviewWindow`：独立模态窗口，`ShowDialog()` 打开
- MainWindow 仅提供入口按钮和依赖传递
- PreviewWindow 自己管理模板列表、显示器列表、fresh read、场景构建和 Canvas 渲染

## fresh read 流程

每次点击"生成预览"执行完整 fresh read：

1. 读取显示器拓扑
2. 解析目标显示器
3. STA 线程：GetCapabilities() + ReadDesktopIcons()
4. 再次读取拓扑检查一致性
5. FailedItemCount > 0 → fail-closed
6. DisplayAssignmentService 分配 MonitorDeviceName
7. DesktopLayoutSnapshotFactory 创建临时快照
8. DesktopLayoutPreviewPlanner.Plan
9. 构建 Scene → 渲染

## 真实 WorkArea 比例

`LayoutPreviewCanvasTransform` 使用单一 scale 等比例缩放，
letterbox 留白。支持 16:9/16:10/4:3/竖屏/负坐标。

## 安全断言

- PreviewPlan 仅用于展示，不是写入授权
- 本窗口无执行按钮
- 不保存安全快照，不创建恢复会话
- 唯一允许的定位接口调用是只读 GetCapabilities
- 本窗口永远不能调用 MoveDesktopIcon
- Writer 阶段仍需 fresh re-read 和全套安全门禁

## 人工 GUI 验收清单

1. 主窗口出现"布局预览"按钮
2. 模态窗口打开
3. 模板列表可加载
4. SelectedTarget 可选择显示器
5. PrimaryDisplay 自动解析
6. ExactDeviceName 正确解析或报错
7. 点击生成后读取实时桌面
8. 16:9 显示器 Canvas 为 16:9
9. 正方形窗口中 Canvas 不被拉伸
10. 调整窗口大小仍保持真实比例
11. Zone 与模板一致
12. 已就位图标状态正确
13. 需要移动图标显示 current/target/line
14. Unassigned 显示在表格
15. OutOfScope 显示在独立列表
16. 切换模板清除旧预览
17. 关闭窗口后桌面图标完全没有变化

## M2-03C 如何在 PreviewPlanner 前加入规则

M2-03C 可以在 `DesktopLayoutPreviewPlanner.Plan` 调用前插入图标过滤/分类层，
按 ItemKind、MonitorDeviceName、用户自定义规则筛选图标后再传入 Planner。
