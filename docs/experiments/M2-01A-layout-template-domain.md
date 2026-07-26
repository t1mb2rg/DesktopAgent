# M2-01A：分区与模板领域模型

## 阶段目标

建立桌面布局模板的纯领域模型：归一化坐标、显示器绑定、分区布局、模板聚合。
所有模型均为纯数据，不依赖 Shell COM、WPF、SQLite、显示器 API 或文件系统。

## 为什么使用 0..10000 整数归一化坐标

工作区像素坐标因显示器分辨率、DPI 缩放、任务栏位置不同而变化。
将矩形统一映射到 `[0, 10000]` 整数范围，模板定义一次后适用于任何显示器。

- 比例计算：`pixelX = (normalizedX / 10000) × workAreaWidth + workAreaLeft`
- 整数避免浮点累积误差和跨平台不一致
- 10000 提供足够精度（约 0.01% 单位）

## 为什么相对于 WorkArea，而不是 Bounds

`WorkArea` 排除任务栏和系统托盘区域，是桌面图标实际可放置的空间。
相对于 `Bounds`（包含任务栏占用）的归一化坐标会产生不可用的目标像素坐标。

## 为什么不用 double

- double 在不同 CPU 架构上可能产生不同的舍入
- 确定性要求：相同模板 + 相同显示器 → 相同像素坐标
- 整数运算可在 checked 上下文中精确检测溢出

## 为什么 NormalizedRect 使用 sealed record 而非 struct

C# 结构体可通过 `default(NormalizedRect)` 或 `new NormalizedRect()`
绕过四参数构造函数，得到 `(0,0,0,0)` 的非法零面积矩形。

`sealed record`（引用类型）的 `default` 为 `null`，
调用方无法创建不经过构造函数验证的实例。

- `NormalizedRect` 为引用值对象，相等性由 record 编译器生成的
  `Equals`/`GetHashCode`/`==`/`!=` 提供（基于全部四个坐标字段）
- `Overlaps` 和 `FullyContains` 对 `null` 参数显式抛出 `ArgumentNullException`
- `DesktopLayoutZone` 构造函数对 `Bounds` 参数做 `null` 检查

## 半开矩形语义

`[Left, Right) × [Top, Bottom)`：包含左/上边界，排除右/下边界。

- `Left` 坐标点属于矩形，`Right` 坐标点属于相邻矩形
- 相邻分区的 Right 与下一分区的 Left 相等 → 不重叠
- 与 `DesktopIconLayoutGeometry.Overlaps` / `PixelRect.Contains` 语义一致

## 为什么 M2 初期禁止分区重叠

重叠分区产生图标分配的歧义。后续 M2 阶段可能引入：
- 优先级熔断规则（如较小的 MaxItemCount 优先）
- 用户通过 WPF 编辑器选择歧义目标

初期严格禁止，出现重叠时 Domain 模型直接拒绝构造。

## 显示器绑定三种模式

| 模式 | 含义 | DeviceName |
|---|---|---|
| `SelectedTarget` | 用户在 WPF 中选择的显示器 | null |
| `PrimaryDisplay` | 系统主显示器 | null |
| `ExactDeviceName` | 固定设备名 | 原始字符串（Ordinal） |

### ExactDeviceName 的稳定性限制

- 不做 Trim，不做大小写转换
- 身份语义为 `StringComparison.Ordinal`
- 如果用户重新安装显卡驱动、更换显示器、或插拔显示器后
  系统重新分配设备名，此绑定将不再匹配
- 这是 M2 阶段的已知限制，后续可扩展 `DevicePath` 或 `HardwareId`

## 模型字段和构造不变量

### NormalizedRect

| 字段 | 约束 |
|---|---|
| Left, Top | ≥ 0 |
| Right, Bottom | ≤ 10_000 |
| Width, Height | > 0 |

### TargetDisplayBinding

| 方法 | Kind | DeviceName |
|---|---|---|
| ForSelectedTarget() | SelectedTarget | null |
| ForPrimaryDisplay() | PrimaryDisplay | null |
| ForExactDeviceName(name) | ExactDeviceName | name (非 null 非空白) |

唯一公开构造路径为三个静态工厂。

### DesktopLayoutZone

| 字段 | 约束 |
|---|---|
| ZoneId | ≠ Guid.Empty |
| Name | 非 null、非空、非空白 |
| SortOrder | ≥ 0 |
| PlacementFlow | Enum.IsDefined |
| MaxItemCount | null 或 > 0 |

### DesktopLayoutTemplate

| 字段 | 约束 |
|---|---|
| TemplateId | ≠ Guid.Empty |
| Name | 非 null、非空、非空白 |
| Version | > 0 |
| TargetDisplayBinding | ≠ null |
| Zones | ≥ 1, 无 null 元素, ZoneId 唯一, SortOrder 唯一, 分区不重叠 |

## 防御性复制

- `DesktopLayoutTemplate` 构造时对输入 `zones` 执行 `ToList()` + `AsReadOnly()`
- 返回的 `Zones` 为 `ReadOnlyCollection<DesktopLayoutZone>`，不可通过 `IList` 接口修改
- 调用方修改原始 `List<DesktopLayoutZone>` 不影响已构造的 Template
- Zone 本身是 `record`（不可变），所以深复制不需要

## 本阶段边界

**仅实现**：

- 6 个核心领域模型（Core 项目）
- 4 个纯内存单元测试类（Tests 项目）
- 1 个实验文档（docs/experiments）

**不实现**：

- SQLite 持久化
- WPF 编辑器
- 归一化坐标 ↔ 像素坐标解析器
- 分区容量计算
- 图标到分区的分配算法
- Writer / Coordinator 集成
- WindowsInterop / Persistence / Poc 项目修改

## M2-01B / M2-02A 后续职责

### M2-01B（模板 SQLite 持久化）

- 模板和分区存储接口
- SQLite 表结构
- 保存、读取、列表和删除
- 模板版本完整往返
- 事务原子性和数据一致性验证
- 不实现像素解析、图标分配或 Writer 执行

### M2-02A（分区解析与容量计算）

- NormalizedRect 相对于目标显示器 WorkArea 解析为 PixelRect
- 定义整数舍入和边界钳制规则
- 根据当前图标间距计算分区网格槽位
- 验证分区物理矩形合法性
- 计算理论容量和 MaxItemCount 有效容量
- 不执行图标到分区的分配

### 后续阶段

- **M2-02B**：图标到分区的确定性分配
- **M2-02C**：分区内具体目标坐标规划
