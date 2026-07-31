# M2-03A：WPF 桌面布局模板编辑器

## 阶段目标

实现独立的 WPF 模态窗口编辑器，用于创建、加载、编辑、保存和删除
`DesktopLayoutTemplate`。编辑器持久化到 SQLite，通过读写验证确保数据完整性。

## 为什么使用独立模态窗口

- 模板编辑是独立的配置工作流，不与 M1 的实机受控操作耦合
- `ShowDialog()` 模态可防止编辑过程中 MainWindow 触发实机写入
- 编辑器每次打开都是新 Window 实例，不注册为 Singleton
- 编辑器关闭后 MainWindow 继续正常操作

## 为什么不重构 M1 MainWindow

- MainWindow 包含 M1 的 Writer、安全快照、恢复会话代码
- 将模板编辑器合并进 MainWindow 会引入耦合
- 保持 M1 代码稳定，模板编辑器独立演进

## DI 注册

在 `App.xaml.cs` 注册：

```csharp
services.AddSingleton<IDesktopLayoutTemplateStore>(
    new SqliteDesktopLayoutTemplateStore(
        DesktopAgentDataPaths.DefaultDatabasePath));
```

## 编辑器入口门禁

`OpenTemplateEditorButton` 在 `UpdateInteractiveState()` 中控制：

- 仅在无操作进行中、无活跃 MoveRecoverySession、
  无 `layoutRestoreRequiresAttention` 时启用
- 点击后传入 `_templateStore` 和 `knownDeviceNames` 创建新窗口

## 可编辑 Draft 与不可变领域模型的边界

- `LayoutZoneEditorDraft` / `LayoutTemplateEditorDraft`：可编辑、允许暂时无效、实现 INotifyPropertyChanged
- `DesktopLayoutZone` / `DesktopLayoutTemplate`：不可变 record，严格验证，构造失败抛异常
- Draft 调用 `TryBuild()` 转为领域模型，无效时返回 Errors
- 不绕过领域构造函数验证

## TemplateId / ZoneId 身份保持

- `CreateNew()` 生成新 Guid
- `FromTemplate()` 完整保留原始 TemplateId 和所有 ZoneId
- 编辑过程中不重新生成 ID
- 保存时模板 ID 不变

## 不 Trim / 不大小写转换

- `ExactDeviceName`、`Name`、Zone `Name` 均保留原样
- 身份比较使用 `StringComparer.Ordinal`

## NormalizedRect 0..10000

归一化坐标平面 0..10000，半开矩形 `[Left,Top,Right,Bottom)`。

## Zone 画布映射

画布是归一化编辑器（不是像素预览）：

```
canvasX = Left / 10000.0 * Canvas.ActualWidth
canvasY = Top / 10000.0 * Canvas.ActualHeight
canvasWidth = Width / 10000.0 * Canvas.ActualWidth
canvasHeight = Height / 10000.0 * Canvas.ActualHeight
```

反向映射使用累计像素 delta，非逐个 Delta 事件。

## 100 单位鼠标吸附

`NormalizedZoneEditorGeometry.SnapDelta(delta, 100)` 使用最近倍数，
中点采用 `MidpointRounding.AwayFromZero`。

## 鼠标移动

- 拖动 Zone Thumb 触发移动
- 保持 Width/Height
- 将矩形 clamp 在 0..10000
- 使用 long 中间运算

## 四角缩放

- TopLeft、TopRight、BottomLeft、BottomRight
- 保持对角固定
- 所有边界在 0..10000
- Width/Height >= `DefaultMinimumSize` (100)
- 使用 long 中间值和 checked

## 拖动时拒绝重叠

- 拖动过程实时检查候选位置是否与其他有效 Zone 重叠
- 重叠时保持上一个有效值，状态栏提示"分区重叠，变更已拒绝"
- 相邻边界不算重叠
- 属性输入可暂时无效，保存时 fail-closed

## 属性输入可暂时无效

- 通过 WPF Binding 的 `ValidatesOnExceptions=True` 处理数字解析
- 保存前检查 `Validation.GetHasError`

## 保存时 fail-closed

1. 提交 WPF Binding
2. 检查 Validation Error
3. `TryBuild()`
4. `SaveAsync()`
5. `GetAsync()` 回读
6. 完整语义比较（Template + 每个 Zone 逐个字段）
7. 一致才 `MarkSaved()`

## 新 Zone 确定性空位扫描

`TryFindFreeRect` 从大到小尝试候选尺寸（2500→2000→1000→500），
步长 250，从左到右、从上到下扫描。返回第一个不重叠的矩形。

## 不自动重排 SortOrder

- 从模板加载时保留原始 SortOrder
- 删除 Zone 后不重排其他 Zone 的 SortOrder
- 新 Zone 使用 `max(existing.SortOrder) + 1`

## 至少保留一个 Zone

- `Zones.Count == 1` 时删除按钮禁用

## SQLite Save + Get 回读验证

保存后立即 `GetAsync` 回读，对模板和每个 Zone 做完整语义比较：

- TemplateId、Name（Ordinal）、Version、BindingKind、DeviceName（Ordinal）
- Zones.Count、每个 Zone 按输入顺序比较全部字段

不一致时抛出异常，保持 dirty。

## Delete + Get null 验证

删除后 `GetAsync` 验证返回 null，否则提示失败。

## dirty tracking

- 模板属性变化、Zone 属性变化、添加/删除 Zone 均触发 dirty
- 正确订阅和取消订阅 Zone 的 PropertyChanged
- `MarkSaved()` 后 `IsDirty = false`

## 未保存切换与关闭保护

- 切换模板时若 dirty，弹出 Yes/No 确认
- 关闭窗口时若 dirty，弹出 Yes/No 确认
- 不提供异步"保存并关闭"

## WPF Binding 错误处理

数字属性使用 `UpdateSourceTrigger=PropertyChanged`、`ValidatesOnExceptions=True`、
`NotifyOnValidationError=True`。

## 自动测试不创建 WPF Window

新测试仅覆盖纯逻辑类型：
- `NormalizedZoneEditorGeometry`
- `LayoutTemplateEditorDraft` / `LayoutZoneEditorDraft`

不创建 Window、Application、Dispatcher、不调用 SQLite 或 MessageBox。

## 本阶段不实现图标预览

归一化编辑画布明确标注"不是图标像素预览"。
M2-03B 的像素预览在后续阶段实现。

## M2-03B 如何消费模板和 PlacementPlan

- 编辑器保存的模板被 M2-02A 解析为 `ResolvedDesktopLayoutTemplate`
- M2-02B 执行分配 → M2-02C 生成目标坐标
- M2-03B 可消费 PlacementPlan 并渲染像素级预览

## 未接入 Writer

- 编辑器只能写模板 SQLite 数据
- 不能调用 `DesktopIconPositionWriter`

## 未移动真实桌面图标

编辑器不读取、不移动桌面图标。

## 后续人工 GUI 验收清单占位

- [ ] 新建模板
- [ ] 加载已有模板
- [ ] 修改模板属性和 Zone 属性
- [ ] 鼠标拖动移动 Zone
- [ ] 四角缩放 Zone
- [ ] 拖动过程重叠拒绝
- [ ] 添加/删除 Zone
- [ ] 保存 → 回读验证
- [ ] 删除 → null 验证
- [ ] 未保存切换确认
- [ ] 未保存关闭确认
- [ ] ExactDeviceName 模式
- [ ] 操作进行中禁用按钮
- [ ] 输入非法字符不崩溃
