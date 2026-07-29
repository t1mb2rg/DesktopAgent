# M2-02A：归一化分区解析与容量计算

## 阶段目标

将 `DesktopLayoutTemplate` 中的归一化分区解析为像素坐标，
并在解析后的物理分区上计算行列数和容量。
纯 Core 计算，不依赖 Shell COM、WPF、SQLite 或显示器 API。

## 为什么相对于 WorkArea 而非 Bounds

`WorkArea` 排除任务栏和系统托盘区域，是桌面图标实际可放置的空间。
`Bounds` 包含不可放置图标的区域，直接映射会产生无效目标像素坐标。

## 统一 floor 边界映射公式

所有边界（Left、Top、Right、Bottom）使用完全相同的公式：

```
pixel = workAreaStart + floor(normalizedValue * workAreaLength / Scale)
```

其中 `workAreaLength` 使用 `long`：

```
waWidth  = (long)workArea.Right - workArea.Left
waHeight = (long)workArea.Bottom - workArea.Top
```

- `normalizedValue` 和 `NormalizedRect.Scale` 为非负整数（领域模型保证）
- `workAreaStart` 为 `workArea.Left`（X 轴）或 `workArea.Top`（Y 轴）
- 乘法 `normalizedValue * workAreaLength` 使用 `long` 避免整数溢出
- 整数除法在 C# 中为向零截断，因操作数均非负故等价于 floor
- 最终加法使用 `long`，然后 `checked` 转换为 `int`

## 为什么不能 Left floor / Right ceil

`Left` 使用 floor + `Right` 使用 ceil 会产生不一致：
- `Left=0, Right=10000` 在 `floor(0)+ceil(10000)` 时宽度可能超过 `waWidth`
- 同一公式保证：相邻分区的 `Right` 精确等于下一分区的 `Left`
- `Right=10000` 映射到 `waStart + waWidth`（即 WorkArea 的精确右边界）

## 为什么使用 long 中间运算

- 工作区宽度可达 `int.MaxValue`（约 21 亿像素，远超实际需要）
- `normalizedValue * workAreaLength` 最大为 `10000 × int.MaxValue ≈ 2.15 × 10^13`
- 该值远小于 `long.MaxValue`（约 `9.22 × 10^18`）
- 不使用 `double` 避免 IEEE 754 舍入和跨平台不一致

## 负虚拟桌面坐标支持

公式中 `workAreaStart` 可以为负数（副屏位于主屏左侧或上方）。
`workAreaLength` 始终为正（`Right - Left` 对有效 WorkArea 为正）。
`offset` 始终非负（归一化值非负），`pixel = workAreaStart + offset` 对负起点语义正确。

## 像素坍缩时 fail-closed

极窄或极矮的归一化分区在当前显示器分辨率下可能映射为 0 像素宽或高。
Resolver 检测 `pixelRight <= pixelLeft` 或 `pixelBottom <= pixelTop` 后
抛出 `InvalidOperationException`，消息包含：
- TemplateId
- ZoneId
- Zone Name
- 原 NormalizedRect
- WorkArea
- 映射后的边界值

不自动扩展到 1 像素，不吞掉该分区。

## 分区局部网格锚点

每个分区的局部网格起点定义为：

```
PixelBounds.Left
PixelBounds.Top
```

柱数和行数从此起点开始按 `iconSpacing` 步进计算。
本阶段仅计算行列数和容量，不枚举具体坐标。

M2-02C 将使用相同锚点和 `PlacementFlow` 生成具体目标坐标。

## ColumnCount / RowCount 公式

```
ColumnCount = PixelBounds.Width / iconSpacing.X
RowCount    = PixelBounds.Height / iconSpacing.Y
```

- 整数除法（向零截断），只计算完整图标单元
- 余数像素丢弃，不使用向上取整
- 结果为 0 时合法（分区容量为零）

## 理论容量与有效容量

```
TheoreticalCapacity = checked((long)ColumnCount * RowCount)
EffectiveCapacity   = Zone.MaxItemCount.HasValue
    ? Math.Min(TheoreticalCapacity, Zone.MaxItemCount.Value)
    : TheoreticalCapacity
```

- `checked` 确保即使 ColumnCount/RowCount 极大也不会静默溢出
- 两种容量使用 `long` 返回，支持超大分区

## MaxItemCount 语义

- `null`：不限制容量，`EffectiveCapacity = TheoreticalCapacity`
- 有值：硬上限，`EffectiveCapacity = Min(TheoreticalCapacity, MaxItemCount)`
- 不改变 `ColumnCount` 或 `RowCount`
- 大于 `TheoreticalCapacity` 时不扩大有效容量

## 零容量分区为何允许

分区可能因显示器分辨率过低而宽度或高度不足以容纳一个完整图标单元。
零容量分区仍返回有效的 `ResolvedDesktopLayoutZone`（`HasCapacity = false`）。
后续 M2-02B 分配阶段将跳过该分区。

## 输入顺序与 SortOrder 的区别

- Resolver 按 `template.Zones` 的当前输入顺序解析
- 不按 `SortOrder` 排序
- `SortOrder` 是规划器使用的显式确定性字段，维持原样
- 输入顺序独立于 SortOrder，两个字段均可独立设置

## Resolved 模型自身也使用 long 验证

`ResolvedDesktopLayoutZone` 和 `ResolvedDesktopLayoutTemplate` 都独立使用 `long` 验证
`PixelRect` 和 `WorkArea` 的宽高，不依赖 `PixelRect.Width`/`Height` 属性
（其 `int` 减法在极端坐标下会溢出）。

公开模型必须独立维护不变量，不能仅依赖 Resolver 已验证。

`ResolvedDesktopLayoutTemplate` 还核验每个 Zone 的 `ColumnCount`/`RowCount`
与 `PixelBounds`/`IconSpacing` 的派生关系，防止构造出自洽但容量语义错误的结果。

## Resolver 与模型双层 fail-closed

- **Resolver 层**：映射后立即检测像素坍缩，拒绝返回
- **模型层**：构造时独立验证所有不变量（矩形合法性、行列数派生、容量一致性）

## 防御性复制

`ResolvedDesktopLayoutTemplate` 构造时对输入 `zones` 执行 `ToList()` + `AsReadOnly()`。
返回的 `Zones` 为 `ReadOnlyCollection<ResolvedDesktopLayoutZone>`，
不可通过 `IList` 接口修改。

## 本阶段不读取 Explorer

- 不调用 `LVM_GETITEMSPACING`
- 不查询 `IFolderView2`
- 不枚举 `IShellFolder`
- 真实的 `spacing` 参数由调用方通过 `DesktopIconPositioningCapabilities` 提供

## Align to grid / 全局网格相位

Explorer 的实际对齐行为可能受到全局网格相位影响，
其与分区局部网格起点 `PixelBounds.Left / Top` 之间是否存在偏差，
需要在后续真实机阶段验证。

通过对比 `MoveDesktopIcon` 返回的 `ActualPosition`
与分区局部网格坐标来测量偏差。

## 本阶段边界

**实现**：
- `ResolvedDesktopLayoutZone`（解析后的单个分区）
- `ResolvedDesktopLayoutTemplate`（解析后的完整模板）
- `DesktopLayoutTemplateResolver`（纯 Core 解析器）
- 73 个单元测试
  - Resolver：41
  - Resolved 模型：32
- 实验文档

**不实现**：
- 图标到分区的分配
- 分区内具体目标坐标规划
- WPF 预览
- Writer 执行
- 真实机验证

## M2-02B 与 M2-02C 后续职责

### M2-02B（图标到分区确定性分配）

- 输入：`ResolvedDesktopLayoutTemplate` + 图标列表
- 按分区顺序和 PlacementFlow 分配图标
- 跳过零容量分区
- 处理图标数量超过总有效容量

### M2-02C（分区内具体目标坐标规划）

- 输入：分配后的分区 + 图标列表
- 按 PlacementFlow 生成具体 `PixelPoint` 坐标
- ColumnMajor：纵向优先（列满后移至下一列）
- RowMajor：横向优先（行满后移至下一行）
