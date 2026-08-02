# M2-03B-A：纯 Core 布局预览与位置差异计划

## 阶段目标

在 M2-03A 模板编辑器基础上，串联 M2-02A/B/C 的解析-分配-坐标流水线，
为给定模板、显示器集合和图标快照生成完整的位置差异预览。

纯 Core 计算，不依赖 WPF、SQLite、Writer。

## 为什么先做纯 Core

- M2-03B-B 的 WPF 预览窗口需要确定性的 Pure-Core 差异数据源
- 将显示目标解析、图标筛选、坐标对比逻辑从 UI 中分离
- 后续 Writer 执行阶段只能复用算法和模型，不能直接执行旧 PreviewPlan；Writer 前必须 fresh re-read、重新解析显示器和图标、重新规划、重新通过能力/安全快照/恢复会话门禁

## 与 M2-02A/B/C 的组合关系

```
DesktopLayoutPreviewPlanner.Plan(template, displays, targetName, spacing, icons)
  │
  ├─ DesktopLayoutTargetDisplayResolver.Resolve(binding, displays, targetName)
  │     → DisplayDescriptor targetDisplay
  │
  ├─ 筛选 TargetDisplayIcons + OutOfScopeIcons
  │     (MonitorDeviceName == targetDisplay.DeviceName, Ordinal)
  │
  ├─ DesktopLayoutTemplateResolver.Resolve(template, workArea, spacing)
  │     → ResolvedDesktopLayoutTemplate          [M2-02A]
  │
  ├─ DesktopLayoutAssignmentPlanner.Plan(resolved, targetIcons)
  │     → DesktopLayoutAssignmentPlan             [M2-02B]
  │
  ├─ DesktopLayoutCoordinatePlanner.Plan(assignmentPlan)
  │     → DesktopLayoutPlacementPlan              [M2-02C]
  │
  └─ 对每个 TargetDisplayIcon 构造 DesktopLayoutPreviewItem
        → DesktopLayoutPreviewPlan
```

## TargetDisplayBinding 三种解析规则

### SelectedTarget

- `selectedTargetDeviceName` 不能为 null/空/空白
- 在 displays 中按 `StringComparer.Ordinal` 精确匹配
- 0 匹配 → 失败；多匹配 → 由重复 DeviceName 验证提前拒绝

### PrimaryDisplay

- 恰好 1 个 `IsPrimary == true`
- 0 → 失败；>1 → 失败
- 不依赖 `selectedTargetDeviceName`

### ExactDeviceName

- 使用 `binding.DeviceName`，按 `StringComparer.Ordinal` 精确匹配
- 0 匹配 → 失败
- 不依赖 `selectedTargetDeviceName`

不做静默回退到第一块显示器。

## DeviceName Ordinal 身份语义

- DeviceName 使用 `StringComparer.Ordinal`
- 不 Trim
- 不做大小写转换
- 重复 DeviceName fail-closed

## 目标显示器图标筛选

`MonitorDeviceName` 与 `TargetDisplay.DeviceName` 按 `StringComparer.Ordinal` 精确匹配的图标进入 `TargetDisplayIcons`。
null `MonitorDeviceName` 不属于任何显示器 → 进入 `OutOfScopeIcons`。

## OutOfScopeIcons 语义

`InputIcons` 中不匹配目标显示器的图标。保持 `Ordinal ASC`，不参与容量计算，无 TargetPosition。

`InputIcons == TargetDisplayIcons + OutOfScopeIcons`（精确分割，不重不漏）。

## 当前/目标坐标差异

```
CurrentPosition = Icon.PixelPosition
TargetPosition  = Placement.TargetPosition
```

## long DeltaX/DeltaY

```
DeltaX = (long)TargetPosition.X - CurrentPosition.X
DeltaY = (long)TargetPosition.Y - CurrentPosition.Y
```

不使用 `int` 直接计算，避免极端坐标差溢出。

## AlreadyAtTarget

`CurrentPosition == TargetPosition` → `State = AlreadyAtTarget`，`DeltaX/Y = 0`。

## MoveRequired

`CurrentPosition != TargetPosition` → `State = MoveRequired`，`DeltaX/Y` 为差异值。

## Unassigned

容量不足的图标 → `State = Unassigned`，无 `TargetPosition`，无 `DeltaX/Y`。

## 容量不足不抛异常

`IsComplete = false`，已分配图标仍有合法 `TargetPosition`。

## IsComplete / HasChanges / IsNoOp

- `IsComplete = PlacementPlan.IsComplete`
- `HasChanges = MoveRequiredCount > 0`
- `IsNoOp = IsComplete && MoveRequiredCount == 0 && UnassignedCount == 0`

目标显示器没有任何图标时：`IsComplete = true`，`IsNoOp = true`，所有计数为 0。

## 负虚拟桌面坐标

支持 `WorkArea` 包含负坐标（副屏在主屏左侧或上方）。

## 不分类、不应用规则

本阶段不按文件类型、系统项目等分类图标。
不实现 pin/exclude/override 规则。

## 不涉及 Writer

不生成移动命令、不调用 `MoveDesktopIcon`、不保存安全快照。

## 不移动真实图标

纯数据计算，零副作用。

## M2-03B-B 如何消费 PreviewPlan

- 读取 `Items` → 渲染每个图标的目标位置和当前位置标记
- 读取 `HasChanges` / `IsComplete` → 控制 UI 状态
- 读取 `DeltaX/Y` → 显示移动向量
- 读取 `OutOfScopeIcons` → 提示用户不被模板管理
- 读取 `TargetDisplay` → 在预览中标注工作区边界

## 安全边界

- PreviewPlan 仅用于只读展示
- PreviewPlan 不是写入授权
- 不得直接执行旧 PreviewPlan
- Writer 前必须 fresh re-read
- 必须重新解析显示器和图标
- 必须重新规划
- 必须重新通过能力、安全快照和恢复会话门禁
- 后续执行阶段复用的是算法和模型，不是陈旧计划实例

## 真实显示器纵横比将在 WPF 阶段展示

M2-03B-B 的 WPF 画布可以使用 `TargetDisplay.WorkArea` 的实际像素尺寸，
在预览中反映显示器的真实纵横比。
