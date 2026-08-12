# Tessel Desktop Radar Spec

> Status: FROZEN product visualization specification  
> Recorded: 2026-08-12  
> Scope: Overview Desktop Radar and reusable spatial visualization language

Desktop Radar 是 Tessel 用来描述“当前桌面空间”的轻量可视化组件。

它不是 Explorer 桌面的缩略复制，也不是布局编辑器的只读截图。它的目标是用稳定、抽象、低成本的方式回答：

```text
我的显示器现在是什么结构？
当前布局怎样划分空间？
桌面项目主要集中在哪里？
哪些区域比较拥挤？
我可以从哪里进入对应布局 / 分区？
```

## 1. 核心产品语义

Desktop Radar 使用三层空间语义：

```text
Display Geometry
    ↓
Layout / Zone Layer
    ↓
Desktop Density Layer
```

它服务于“看懂”和“定位”，不是执行证据。

Radar 中的任何数据都不得成为 Writer、Recovery、Planner 或 Automation Authority 的事实来源。

## 2. 展示数据边界

建议从完整 `DesktopObservation` 压缩出专用 presentation snapshot：

```text
Fresh DesktopObservation
        ↓
DesktopRadarSnapshot Builder
        ↓
DesktopRadarSnapshot
        ↓
Overview Read Model / Radar Renderer
```

Radar Snapshot 不保存：

- 文件名；
- 文件路径；
- PIDL；
- 可执行 item identity；
- Writer target coordinate payload；
- 可恢复真实桌面状态的副本。

它只保留展示所需的：

- display geometry / aspect ratio；
- item total count；
- density grid；
- observed timestamp；
- display topology fingerprint；
- 在 layout / version 完全匹配时可选的精确 zone count summary。

建议在类型 / 注释中显式声明：

```text
Presentation-only.
Must not be used as execution evidence.
```

## 3. 显示器几何

Radar 尽量保持真实显示器宽高比和相对方向感。

多显示器时，每块显示器作为独立 Radar surface：

```text
┌─────────────────────┐     ┌──────────────┐
│ 主显示器             │     │ 副屏         │
│                     │     │              │
│ Zone A     Zone B   │     │ Zone C       │
└─────────────────────┘     └──────────────┘
```

竖屏、超宽屏等应在视觉上保持可辨认的比例，不强行绘制成统一矩形卡片。

## 4. Radar Mark

桌面项目不复刻真实 Windows 文件图标。

第一版使用统一的 Tessel Radar Mark：

- 抽象轻量几何符号；
- 中性色；
- 轻微网格感；
- 不表达文件类型；
- 不表达文件身份；
- 不显示文件名；
- 不作为独立点击目标。

Mark 的位置来自真实桌面项目坐标的低分辨率空间聚合。

`数字` 回答“多少”，Radar Mark 回答“在哪里”。

## 5. Density Mark 视觉规则

一个 density cell 对应一个 Radar Mark。

Mark 使用有限档位的尺寸与透明度表达拥挤程度：

```text
Empty
→ 不绘制

Low density
→ 小、淡

Medium density
→ 中等尺寸、中等透明度

High density
→ 稍大、较实
```

具体像素尺寸和 occupancy 阈值在实现 / physical tuning 时确定，不在设计阶段凭空冻结。

约束：

- 大小和 opacity 都只表达同一个概念：density；
- 不让颜色、大小、透明度分别承载不同数据；
- size 很快封顶，高密度不能膨胀成巨大圆点；
- 不通过疯狂增加 Mark 数量表达高密度；
- Zone hover 时可以整体提高 Mark opacity，但不改变 density 语义。

第一版优先“一 Cell 一 Mark”，不引入复杂 cluster glyph。

## 6. Adaptive Radar Tessellation

Radar Grid **不使用固定全局行列数**。

每块显示器独立选择合适的 grid resolution。

自动选择依据：

```text
显示器长宽比
+
桌面项目总量
+
项目实际空间分布
```

目标不是追求数学精度，而是找到“刚好能看见空间纹理”的尺度：

- 不能粗到只剩几个巨大 Cell；
- 不能细到绝大多数 Cell 永远为空；
- 不能让大量项目长期压进同一个 Cell；
- 仍保持可读的桌面网格感。

推荐使用有限候选 resolution tiers，例如概念上的：

```text
Coarse
Medium
Fine
Very Fine
```

具体行列数根据显示器比例生成。

## 7. 一块 Display 内保持统一 Cell 尺寸

第一版不使用 Quadtree / 局部递归分裂。

即：

```text
同一显示器
→ 一个统一 Grid

不同显示器
→ 可以选择不同 Grid Resolution
```

这样可以保持：

- 空间尺度一致；
- Mark 大小可比较；
- 网格感稳定；
- Renderer 简单；
- 动画和 hover 容易理解。

## 8. Density Band 与 Grid 联合计算

Density threshold 不使用固定全局值，例如不能永久写死：

```text
1-2 = Low
3-5 = Medium
6+ = High
```

因为 grid resolution 改变后，同一桌面内容会产生完全不同的 per-cell occupancy。

因此：

> Grid Resolution 和 Density Band 必须作为同一套自适应计算的一部分。

Density band 应综合：

- cell 的绝对拥挤程度；
- 当前 display 的 occupancy distribution。

不能纯 percentile，否则一个整体极度拥挤的桌面可能把“10 items / cell”错误解释成 Low。

用户界面不显示 Low / Medium / High 图例。拥挤程度应直接通过 Mark 的 size / opacity 被感知。

## 9. Grid Stability / Hysteresis

Adaptive Grid 必须有迟滞，避免桌面增加一个项目就让整个 Radar 改变尺度。

原则：

```text
当前 Grid 仍能合理表达空间
→ 保持

只有结构明显变化
→ 调整一个 resolution tier
```

Radar Snapshot 可以记住上次使用的：

```text
GridColumns
GridRows
```

下一次生成时先评估旧 Grid 是否仍可接受，只有评分明显越界才升级 / 降级。

视觉稳定优先于追求每次 Observation 的数学最优网格。

## 10. Zone Layer

Zone 是 Radar 的主要空间交互对象。

Zone Layer 显示：

- normalized bounds；
- Zone name；
- 克制的用户 Zone identity color；
- 可选精确 item count，当 snapshot 对应的 layout id / version 与当前 layout 完全匹配时。

Zone 颜色建议：

- 边框：轻量身份色；
- 填充：极淡；
- Radar Mark：保持中性，不随 Zone 颜色整体染色。

颜色表达“空间身份”，Mark 表达“桌面内容密度”。

## 11. 精确数量与空间密度分离

Radar Mark 是空间聚合，不承担精确数量表达。

如果需要显示：

```text
工作区
18 项
```

`18 项` 必须来自可靠的精确统计，而不是反推 density grid。

当 LayoutTemplateId / Version 与生成 Radar Snapshot 时不一致：

- density grid 仍可用于展示桌面空间；
- 旧 exact zone membership summary 不得冒充当前精确数据；
- 可以暂时只显示桌面总数，等待下一次 fresh snapshot。

## 12. Freshness

Radar 首帧优先显示 last-known presentation snapshot。

典型行为：

```text
打开 Overview
↓
立即显示 cached Radar
↓
显示“刚刚更新 / 2 分钟前 / 需要刷新”
↓
必要时后台刷新
↓
平滑更新 Density Layer
```

明确失效信号包括：

- Explorer / taskbar restart；
- display topology change；
- 长时间 sleep / resume；
- 其它明确表明桌面事实可能已经变化的事件。

Layout 发生变化时，Layout Layer 可以立即更新；这不必自动意味着重新读取 Shell。

## 13. Refresh Strategy

禁止高频 polling。

Radar 优先采用事件 / 用户意图 / 已有观察结果驱动：

- cold launch 首帧之后必要时刷新；
- 用户显式刷新；
- Layout Preview 等功能已经完成 Fresh DesktopObservation 时顺便更新；
- Automation / other read path 已经获得 Fresh Observation 时顺便更新；
- Explorer restart 后延迟刷新；
- display topology change 后刷新；
- 多个短时间变化合并 / debounce。

核心原则：

> 如果 Tessel 的其它功能已经为 Fresh DesktopObservation 支付了成本，Radar 应复用该事实并更新 presentation cache，而不是每个页面各自重新读取 Shell。

## 14. Interaction

Radar 的可交互对象：

```text
Display
Zone
```

Radar Mark 不代表单个 item，因此不可单独点击。

导航语义：

```text
点击 Radar 主卡
→ Layout 页面
→ 当前布局

点击 Display
→ Layout 页面
→ 当前布局 + 对应 Display

点击 Zone
→ Layout 页面
→ 当前布局 + 对应 Zone
```

所有上述行为均为 passive navigation / selection：

- 不进入 Layout Editing；
- 不触发 Fresh Writer execution；
- 不触发 Automation；
- 不移动桌面图标。

## 15. 无布局 / 未分区状态

Radar 不依赖 active layout 才存在。

没有布局时仍可显示：

- display geometry；
- desktop density；
- total item count；
- 自然的“还没有设置布局”提示。

Zone 外的项目直接以相同 Radar Mark 出现在 Zone 之外，不发明额外 item glyph。

可以补充：

```text
3 项未分区
```

## 16. Reuse

Radar Renderer 应尽量成为 Tessel 的共享空间视觉语言，而不是 Overview 私有组件。

未来可复用到：

- Overview 当前桌面；
- Layout workbench passive preview；
- AI proposal layout preview；
- Pending layout impact；
- Desktop Zone Editor 的非交互背景层。

不同页面可以叠加不同 interaction / annotation layer，但 Display / Zone / Density 的基础语言保持一致。

## 17. Frozen Design Contract

当前冻结：

> Desktop Radar 使用按显示器独立计算的自适应网格。Grid Resolution 依据显示器比例、桌面项目数量与空间分布从有限候选层级中自动选择，并通过迟滞机制保持视觉稳定。Density Band 与所选 Grid 联合计算，Radar Mark 使用有限档位的尺寸与透明度表达拥挤程度。Radar 只用于展示和被动空间导航，不承载 item identity、Writer evidence 或 automation authority。
