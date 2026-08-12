# Tessel Desktop Radar Multi-monitor Spec

> Status: FROZEN product visualization specification  
> Recorded: 2026-08-12  
> Scope: Desktop Radar multi-monitor topology, fitting and LOD

本规格补充 `Tessel-desktop-radar-spec.md`，冻结 Overview Desktop Radar 在多显示器场景中的展示与交互语义。

## 1. Virtual Desktop Cluster

多显示器 Radar 不把每块显示器拆成独立 Card，也不使用响应式换行破坏空间关系。

所有当前可用显示器共同构成一个：

```text
Virtual Desktop Cluster
```

Overview 优先保留用户真实桌面的：

- 左 / 右关系；
- 上 / 下关系；
- 横屏 / 竖屏方向；
- 大致尺寸差异；
- 显示器之间的垂直错位；
- 哪些显示器彼此相邻。

核心原则：

> Radar 优先保留整个桌面的空间关系，而不是优先保证每块 Display 的信息完整。

## 2. Topology Normalization / Visual Compression

Radar 不要求严格按照 Windows virtual desktop 坐标逐像素绘制。

建议转换：

```text
Windows Virtual Desktop Geometry
        ↓
Topology Normalization
        ↓
Visual Compression
        ↓
Radar Cluster
```

保留真实拓扑语义，同时允许压缩：

- 过大的 Display 间距；
- 无视觉意义的 coordinate offset；
- 极端分辨率造成的面积差。

冻结原则：

> 空间关系是真的，视觉距离不要求一比一。

Display gap 需要限幅，不能因为虚拟坐标差异让 Overview 出现大面积无意义空白。

## 3. Display Geometry

Aspect Ratio 属于硬语义，应尽量保持。

绝对显示面积属于视觉近似，可以为了可读性适度压缩。

例如超宽屏仍应明显比普通横屏更宽，竖屏仍应明显呈竖向，但副屏不能因为严格按像素面积缩放而退化成不可读的小邮票。

需要保护最小可辨认 / 可交互宽度，尤其是竖屏。

DPI scaling 不直接决定 Radar 中 Display 的形状。桌面 geometry 应先进入稳定的 desktop coordinate space，再 normalize 到 Radar coordinate space。

## 4. Display Label

单显示器时默认不显示冗余 `主显示器` 标签。

多显示器时使用轻量、用户可读的 Label：

```text
主显示器
显示器 2
竖屏
```

不在 Overview 常驻显示设备型号、EDID、分辨率等工程信息。

详细 Hover / accessibility 可补充友好名称和分辨率。

主屏不需要夸张 `PRIMARY` Badge 或彩色卡片。

## 5. Display Surface

Display 是桌面画布，不是嵌套 Card。

普通状态：

- 轻量边界；
- 极克制的 theme surface；
- 无额外浮动阴影；
- Free Desktop 留白本身属于有效视觉信息。

无 Zone 的 Display 是合法 Free Desktop，不显示 `未配置`、警告图标或错误状态。

## 6. Cluster Fit

Overview 默认行为：

```text
Fit Virtual Desktop Cluster
```

第一版不提供：

- Radar Zoom + / -；
- 100% 缩放；
- 默认横向滚动；
- 分页；
- 自动换行显示器；
- Hover 放大镜 / Dock 式动态膨胀。

需要精细查看时，用户点击 Display / Zone 进入 Layout Workbench。

## 7. Display LOD

Cluster 缩放后，每块 Display 根据实际 rendered bounds 独立选择信息密度。

概念等级：

```text
Full
→ Display Label
→ Zone Label
→ Density Marks
→ 可用的精确 Zone Count

Compact
→ Display Label
→ Zone Geometry
→ Density Marks
→ 数量可隐藏

Mini
→ Display Geometry
→ Zone Geometry
→ 极简 Density
→ Zone 文本默认隐藏，Hover / tooltip / accessibility 补足
```

宁可降低信息 LOD，也不能为了保留文字而拆散真实 topology。

LOD 切换需要迟滞，避免窗口尺寸轻微变化导致文本频繁出现 / 消失。

## 8. Nested LOD

多屏 Radar 具有分层自适应：

```text
Cluster Fit
    ↓
Display LOD
    ↓
Zone LOD
    ↓
Adaptive Density Grid
```

不同 Display 可以同时处于不同 LOD。

例如主屏 Full、副横屏 Compact、小竖屏 Mini，不要求全局统一降级。

## 9. 极端多屏

1-3 块屏幕优先完整 topology。

4 块屏幕可自然进入 Compact LOD。

5-6 块屏幕继续保持 topology silhouette，并允许部分 Display 进入 Mini。

当空间非常有限时，优先级为：

```text
Topology
> Display identity / orientation
> Zone geometry
> Density shape
> Zone text
> exact count
```

不能为了“全部信息都有”把显示器缩到不可点击尺寸。

## 10. Interaction

Hover Display 的 Free Desktop 区域：

- 仅轻微增强 Display outline / label；
- 不让整个 Display 像按钮卡片一样大幅变色；
- 可展示简短 tooltip，例如 `主显示器 · 46 项 · 2 个分区`。

点击 Display 空白区域：

```text
Overview
→ Layout
→ Current Layout
→ Target Display
```

点击 Zone：

```text
Overview
→ Layout
→ Current Layout
→ Target Display
→ Target Zone
```

均为 passive navigation，不进入编辑，不调用 Writer。

## 11. Summary 文案

底部 Summary 根据当前桌面自然收缩。

单屏不需要写 `1 块屏幕`：

```text
46 项 · 2 个分区
```

多屏可写：

```text
73 项 · 3 块屏幕 · 3 个分区
```

不使用 `Display 1 / Display 2 / Display 3` 这类工程枚举作为主 Summary。

## 12. Frozen Contract

> Desktop Radar 始终将多显示器视为一个 Virtual Desktop Cluster。优先保持真实拓扑、方向与宽高比，通过视觉压缩控制 Display 间距和极端面积差；当空间不足时降低 Display / Zone 信息 LOD，而不是拆散拓扑、换行、分页或默认滚动。Overview 始终 Fit 整个桌面，精细查看交给 Layout Workbench。
