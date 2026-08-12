# Tessel Overview Page Spec

> Status: product UI specification  
> Recorded: 2026-08-12  
> Scope: Tessel Overview surface only

概览页是 Tessel 打开后最先让用户看到的产品页面。

它的任务不是把所有工程状态集中到一个 Dashboard，而是回答几个最直接的问题：

```text
我的桌面现在是什么状态？
当前在用哪个布局？
Tessel 现在会自动做什么？
有没有事情等我确认？
我接下来最可能想做什么？
```

## 1. 视觉方向

Tessel 默认跟随 Windows 系统主题，不绑定浅色或深色。

概览页使用：

- 稳定左侧 NavigationView；
- 中央大桌面 / 布局可视化卡片；
- 少量状态卡；
- 最近活动；
- 快速操作；
- Fluent / Windows 11 风格的中性 Surface；
- Zone 颜色作为状态和品牌点缀，不把整个页面做成多色仪表盘。

Tessel Logo 可以使用多色 tile 语言，产品内容区域仍以系统主题和少量 Accent 为主。

## 2. 页面骨架

建议桌面宽度下采用：

```text
┌──────────────────────────────────────────────────────────────┐
│ 概览                                                         │
│ 看看你的桌面现在怎么样。                                     │
├───────────────────────────────────────┬──────────────────────┤
│                                       │ 当前布局              │
│                                       │ 工作                  │
│                                       ├──────────────────────┤
│      当前桌面 / 布局可视化            │ 自动化                │
│                                       │ 辅助模式              │
│                                       ├──────────────────────┤
│                                       │ 待确认                │
│                                       │ 3 项                  │
├───────────────────────────────────────┼──────────────────────┤
│ 最近活动                              │ 快速操作              │
│                                       │                      │
└───────────────────────────────────────┴──────────────────────┘
```

具体列宽不在文档阶段冻结，核心是：

> 桌面可视化必须是视觉主角，状态卡围绕它，而不是反过来。

## 3. 页面标题

建议：

```text
概览
看看你的桌面现在怎么样。
```

不需要在页面正文再次堆品牌口号。

如果顶部 Shell 已经明显显示 `Tessel`，概览内容区不需要再放一个巨大的产品名作为主标题。

## 4. 当前桌面 / 布局主卡

这是概览页最重要的区域。

目标：

> 让用户一眼看懂 Tessel 认为当前桌面的空间结构是什么。

第一版可以显示：

- 当前目标显示器的比例化桌面画布；
- 当前 active layout 的 Zone；
- Zone 名称和颜色；
- 当前布局名称；
- 桌面项目数量；
- 如果有可靠的 lightweight snapshot，可以显示项目的大致分布；
- 没有 active layout 时显示自然的空状态。

第一版不要求：

- 截取真实壁纸；
- 实时绘制所有 Windows 图标缩略图；
- 为了概览强制执行 Fresh DesktopObservation；
- 实例化完整 `LayoutManagementView`。

空状态建议：

```text
还没有正在使用的布局
创建一个布局，或者从已有布局中选一个使用。

[打开布局]
```

## 5. Lightweight Overview Read Model

概览必须拥有自己的轻量只读数据路径。

建议概念模型：

```text
OverviewState
├─ ActiveLayoutSummary
├─ ZoneSummary[]
├─ AutomationMode
├─ PendingCount
├─ DesktopItemCount?       optional / cached
├─ LastActivity[]
└─ UpdatedAt
```

它不应该直接持有：

```text
LayoutManagementView
DesktopLayoutEditDraft
DesktopLayoutImpactPreview
DesktopShellIconPositionWriter
STA executor
Overlay HWND
AI Provider client
```

如果某个数字只有启动昂贵 Shell observation 才能得到，第一版宁可：

- 使用最近一次可靠展示快照；
- 显示“尚未读取”；
- 在用户主动刷新时异步读取；

也不要为了一个首页数字把整条重运行时提前唤醒。

任何 last-known snapshot 都只能作为展示数据，不能成为 Writer 的执行证据。

## 6. 状态卡

第一版建议最多保留三个主要状态卡。

### 当前布局

```text
当前布局
工作
```

如果没有：

```text
当前布局
未选择
```

点击卡片可以进入布局页并选中当前布局。

卡片本身不执行 Apply / Writer。

### 自动化

```text
自动化
辅助模式
```

补充一行可以是：

```text
确认过的长期规则可以自动使用。
```

不要直接显示 `Assisted` enum。

点击进入设置里的自动化区域。

### 待确认

```text
待确认
3 项
```

空状态：

```text
待确认
没有待处理项目
```

点击进入待确认页。

## 7. 桌面项目数量

“桌面项目数”是有用的信息，但不要为了它破坏启动基线。

如果能从已有轻量状态、后台监听或最近 snapshot 低成本得到，可以显示：

```text
桌面项目
128
```

如果必须启动完整 Shell COM observation 才能得到，则它不应成为 Overview 首帧的硬依赖。

这个卡片可以：

- 首帧显示最近数据；
- 后台 idle refresh；
- 或暂缓到后续版本。

## 8. 最近活动

最近活动是用户可读的行为历史，不是 Debug Log。

适合显示：

```text
使用了布局“工作”
保存了布局“课程”
调整了分区颜色
确认了 2 个整理建议
桌面线框已显示
```

不适合显示：

```text
Observation captured
RuleSet V3 persisted
SQLite transaction committed
TaskbarCreated received
WriterCoordinator state = ...
```

第一版如果还没有合适的产品级 activity source，可以显示空状态，不要为了填满页面临时把诊断日志搬进来。

## 9. 快速操作

第一版只放真正高频、语义清楚的动作。

建议候选：

```text
打开布局
显示桌面线框
新建布局
```

未来产品执行链真正接入后，可以再讨论：

```text
整理桌面
```

在 Writer 尚未进入正式产品链之前，不要为了视觉完整放一个假的“立即整理”按钮。

`显示桌面线框` 只显示 click-through Overlay，不进入 Desktop Zone Editing。

未来 M5-07.5 实现后，可另加：

```text
编辑分区
```

它必须明确进入 Layout Editing / Automation BLOCK。

## 10. Pending 提醒

如果 PendingCount > 0，可以在概览里自然提醒：

```text
有 3 项需要你确认
```

不要用危险色制造焦虑，除非 Pending 原因本身代表真正的错误或安全问题。

普通等待确认是工作流状态，不是异常。

## 11. 刷新行为

概览页不需要持续高频刷新。

建议：

- 页面首次显示时读取轻量状态；
- product settings / active layout / pending 等明确变化后推送或失效缓存；
- 多个短时间变化合并刷新；
- 昂贵桌面事实只在需要时刷新；
- 不用固定 100 ms / 1 s timer 为了让 Dashboard 看起来“活着”。

## 12. 首帧和 Hydration

Overview 是 Interactive Shell 首帧的一部分。

优先级：

```text
1. Window / Navigation 首帧
2. Overview 卡片骨架
3. settings / active layout / cached summary
4. pending count / recent activity
5. 可选的较慢展示数据
```

进入 Interactive 时，Overview 应该立即可见。

如果有数据仍在加载，使用稳定 Skeleton / placeholder，而不是冻结整个窗口。

## 13. Resident 生命周期关系

Deep Resident 不需要长期保留完整 Overview Visual Tree。

建议：

```text
Deep Resident
→ 保留轻量 OverviewState / 必要 cache

Tray interaction
→ 开始 Interactive prewarm

Open Tessel
→ 快速创建 / 恢复 Overview UI

Warm Resident
→ 短时间可保留 UI，便于快速 reopen

TTL / memory pressure
→ 释放重 UI，回到 Deep Resident
```

具体 unload 策略属于 Resident Lifecycle Optimization，不在 Overview 页面代码里各自实现一套私有生命周期。

## 14. 页面导航语义

Overview 上的选择和卡片点击属于导航 / 查看，不代表执行。

例如：

```text
点击“当前布局”
→ 打开布局页

点击“待确认 3 项”
→ 打开待确认页

点击桌面主卡
→ 查看当前布局详情
```

这些动作不需要额外确认，也不会改变桌面。

真正改变状态的动作继续使用显式按钮。

## 15. 主题

默认：

```text
跟随 Windows
```

后续设置可以允许：

```text
跟随系统
浅色
深色
```

但第一版不需要为了主题选择重写两套视觉。

尽量使用 WinUI `ThemeResource`，只有品牌 Logo、Zone color 和必要视觉资产使用明确颜色。

深色和浅色的目标不是完全相同的 RGB，而是保持相同的信息层级和视觉重量。

## 16. Accessibility / Reduced Motion

页面不依赖颜色单独表达状态。

Zone 可以使用颜色，但名称 / 标签仍然存在。

未来如果加动效：

- 不用大幅移动页面内容；
- 不用持续动画表示“正在管理桌面”；
- 尊重 Windows reduced motion / accessibility preference；
- Skeleton 和状态更新不应造成明显布局跳动。

## 17. 第一版明确不做

Overview V1 不需要：

- 实时任务管理器式 CPU / 内存仪表；
- 工程 Diagnostics 指标；
- 大量历史图表；
- AI 对话直接铺满首页；
- 壁纸截图；
- 全量 Explorer 图标缩略图；
- 自动执行按钮抢在正式产品 Writer 链之前出现；
- 为了首页数据破坏 Deep Resident 目标。

## 18. 完成标准

Overview 第一版完成时至少满足：

- 打开 Tessel 后能在首屏看懂当前布局和主要状态；
- 桌面 / 布局可视化是页面主角；
- Manual / Assisted / Automatic 不以 enum 原文展示；
- Pending 能一眼看到但不会被渲染成异常；
- 快速操作不会越过现有产品安全边界；
- 页面不构造 Writer；
- 页面不因为首帧展示实例化完整 Layout workbench；
- 深浅主题均可读；
- Overview 的性能成本可以独立测量；
- 后续 M5-07.5 Desktop Zone Editing 有自然入口，但未实现前不显示假功能。
