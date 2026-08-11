# M5-05 Daily Workflow + Layout Management

## 目标

把 M2/M4 已冻结的布局模板、规则、只读桌面观察和预览规划能力接入 WinUI 产品壳，形成可以日常使用的布局管理工作流。

M5-05 只解决“创建 / 编辑元数据 / 预览影响 / 原子保存 / 选择当前布局”。本阶段仍不执行真实桌面移动。

## 产品链路

```text
Persisted Template + RuleSet
            ↓
    Product LayoutDraft
            ↓
Fresh DesktopObservation
            ↓
      Impact Preview
            ↓
 Template + Rules Atomic Save
            ↓
   Active Layout Preference
```

任何未来真实执行都必须从新的 DesktopObservation 重新规划：

```text
Active Layout
    ↓
Fresh DesktopObservation
    ↓
Fresh Replan
    ↓
Automation Policy / Human Gate
    ↓
Writer（后续阶段）
```

Impact Preview 本身不是 execution-ready payload，也不会被保存成可重放 WriterCommand。

## LayoutDraft

`DesktopLayoutEditDraft` 是产品编辑阶段的内存草稿：

- 保留 TemplateId 与来源 Version；
- 新模板保存为 V1；
- 已有模板每次成功保存严格推进一个 Version；
- Preview 与 Save 都从草稿重新构造经过 Core 验证的不可变领域对象；
- 草稿没有 Writer authority。

产品先提供四种常用 Zone 预设：单区、左右两栏、三栏、2×2 四宫格。

若现有 RuleSet 含 ZoneOverride，产品当前拒绝直接用预设替换 Zones，避免已有规则悄悄指向失效 Zone。后续完整规则编辑器可以显式处理此迁移。

## Impact Preview

WinUI 产品壳通过 `WindowsDesktopObservationSource` 每次重新观察真实桌面，再调用纯 Core `DesktopLayoutRuleAwarePreviewPlanner`。

为避免产品壳为了读取 spacing / auto-arrange 而构造 Writer，M5-05 增加独立的 `DesktopShellIconPositioningCapabilitiesReader`。它只读 Shell 能力，不暴露 `MoveDesktopIcon`。

Preview 的主产品视图优先使用图形表达：

- Zone 直接画在桌面画布中；
- Fresh Preview 后用目标点和移动轨迹表现预计变化；
- 主界面只突出“移动 / 保持 / 目标屏”三项摘要；
- ObservationId、Excluded / Pinned、精确坐标等工程信息收进“详细影响”。

## UI 信息架构

M5-05 UI refinement 冻结以下方向：

```text
PowerToys / Fluent 应用骨架
        +
Overview Dashboard
        +
AI 一级入口
        +
Pending Queue
        +
Layout Workbench
        +
Settings
```

布局页改为三栏工作台：

```text
布局列表 | 可视化桌面画布 | 轻量属性
```

产品文案遵循“主界面讲发生了什么，详细信息再讲系统内部为什么”的原则，避免将 Policy、Observation、Writer 等开发合同作为常驻说明墙。

### Selection = passive preview/detail

产品交互原则冻结为：**选中对象立即展示其被动预览 / 详情；显式按钮只保留给真正改变状态、解锁编辑或触发昂贵动作的操作。**

布局页因此区分浏览态与编辑态：

- 单击已保存布局，中央 Zone 画布和右侧属性立即出现；
- 浏览态字段保持只读，但 Fresh Preview / Desktop Overlay 可以直接使用；
- 只有显式点击“编辑”才解锁名称、目标屏、预设与保存，同时进入 `Layout Editing → Automation BLOCK`；
- 编辑中尝试切换其他布局会恢复当前选择，避免未保存修改被静默丢失；
- 异步读取旧 Selection 若已经过期，不得覆盖用户刚切换到的新上下文。

AI 助手先建立一级导航与产品空状态；真实对话能力后续接入时，AI 生成的方案必须产出同一个 `LayoutDraft` 合同，再进入布局工作台继续可视化编辑，而不是另建一套 AI-only 布局模型。

## Desktop Zone Overlay

M5-05 将最初的“会话级细线框”提升为产品化分区框展示层。

### Z-order / 安全边界

首轮真实 Windows Gate 暴露过 `WS_EX_TOPMOST + HWND_TOPMOST` 导致 Overlay 覆盖普通应用并出现卡死退出。Directed Repair 后继续冻结以下边界：

- Overlay 由 `GetShellWindow()` 返回的 Windows Shell desktop window 拥有；
- 明确不使用 `WS_EX_TOPMOST` / `HWND_TOPMOST`；
- layered + no-activate + click-through；
- Shell desktop 不可用时 fail closed，不回退为全局悬浮窗；
- managed 绘制异常不允许穿过 native WndProc 终止产品进程；
- `PAINTSTRUCT` 保持 x64 正确 native 布局；
- Overlay 只消费 `DisplayDescriptor + Zone + Appearance`，不读取或写入图标位置，不拥有 Writer。

真实 Windows 已确认 Z-order Directed Repair 后：普通应用可以自然盖住 Overlay，桌面鼠标操作正常，不再卡死/退出。

### 圆角分区框外观

Overlay 不再只画 1px 辅助线。每个 Zone 以轻量卡片式分区框表达：

- 边框可调粗细；
- 边框透明度与框内填充透明度独立；
- Zone 使用圆角；
- Zone 名称以轻量标签显示；
- Zone 之间留少量视觉间隙，让“区域”比单根分割线更容易理解。

由于传统 `SetLayeredWindowAttributes` 对单个窗口只有一个全局 alpha，边框与填充不能在同一 GDI layered window 中拥有独立透明度。M5-05 因此使用每个目标显示器最多两个轻量 Shell-owned overlay layer：

```text
Chrome layer  → 边框 + Zone label（BorderOpacity）
Fill layer    → 圆角填充（FillOpacity）
```

窗口数量保持固定为每屏最多 2 个，不回退到最初“每个 Zone 多个顶层窗口”的高风险实现。

### 用户外观设置

`DesktopOverlayAppearanceSettings` 为纯视觉设置，不携带执行权限。默认提供：

- 轻量 Light；
- 标准 Standard；
- 明显 Strong；
- 高级参数变化后进入 Custom。

高级自定义首批开放：

- `BorderThicknessDip`：1–8；
- `BorderOpacity`：10%–100%；
- `FillOpacity`：0%–35%；
- `CornerRadiusDip`：0–32。

外观保存在现有 `settings.json` 的可选 `overlayAppearance` 节点中，继续使用 formatVersion=1。旧 M5-04 / 早期 M5-05 设置文件没有此节点时按 Standard 读取，不要求用户清设置。

MainWindow 或布局页保存其他产品偏好时必须保留 OverlayAppearance，避免跨页旧快照把视觉设置覆盖掉。

### 双级预览

设置页参考 PowerToys 的直接反馈方式：

1. **内嵌实时预览**：右侧示意桌面始终显示两个 Zone，拖动参数立即更新；
2. **真实桌面临时预览**：用户显式点击后，在主显示器显示安全的两栏示例框。

真实桌面预览只验证外观，不使用真实图标位置、不执行布局、不生成 WriterCommand。滑块连续拖动时 native Overlay 刷新使用短防抖，避免每个像素都频繁销毁 / 重建窗口。

点击“应用”才写入正式产品设置并更新本进程后续 Overlay 的默认外观；点击“取消”恢复已保存设置并清理真实桌面临时预览。

## Atomic Save

Template 与 RuleSet 仍由两个 Store 管理。M5-05 把原 PoC 的逻辑原子保存协调器提升到 Application 层，产品壳与 WPF Workbench 共用同一个实现：

1. 保存前读取旧 Template + RuleSet；
2. 验证 Template / RuleSet / ZoneOverride 引用；
3. 写 Template；
4. 写 RuleSet；
5. 任一步失败则补偿回旧状态；
6. 成功后精确回读验证。

不再保留 PoC 独立副本，避免两套事务语义漂移。

## Active Layout

`DesktopProductSettings.ActiveLayoutTemplateId` 记录用户当前选择的布局。

这是产品偏好，不代表：

- 当前桌面已经符合该布局；
- 用户授权立即执行；
- AI 获得 Writer authority；
- 自动化可以跳过 Fresh Replan。

此字段作为 formatVersion=1 的可选新增字段持久化，旧 M5-04 settings.json 缺少该字段时继续按 null 读取。

## Pending Queue 产品边界

M5-05 保留 Pending Queue 为只读。

M5-02 Confirm Once / Confirm & Remember 会删除 Pending 项并返回仅供后续 Fresh Execution Pipeline 消费的一次性 `DirectUserRequest` 授权。当前产品壳还没有完整 Fresh Execution Pipeline；提前接确认按钮会造成“项目消失但批准动作未执行”的错误产品语义，因此不提前消费该结果。

## 安全边界

- Layout Editing → Automation Policy BLOCK；
- Preview 只读，不产生 WriterCommand；
- Preview 每次从新鲜 DesktopObservation 生成；
- Desktop Overlay 只显示 Zone / Appearance 元数据，鼠标穿透，不拥有 Writer；
- Overlay 外观设置只写非秘密产品偏好；
- “保存”只保存 Template / Rules 元数据；
- “使用”只保存 ActiveLayoutTemplateId；
- 产品壳不构造 `DesktopShellIconPositionWriter`；
- 不打开 production unattended gate；
- 不修改 Recovery safety semantics；
- 不移动任何真实桌面图标。

## Physical Windows Gate

CI 与 Scope Audit 通过后，在真实 Windows 11 x64 验证：

1. 概览 / AI 助手 / 待确认 / 布局 / 设置五个入口可正常切换；
2. AI 助手页面以产品空状态出现，未连接的发送入口明确 disabled；
3. 单击已保存布局后直接出现 Zone 画布和只读属性，不要求先点“编辑”；
4. 浏览态可直接 Fresh Preview / Desktop Overlay；显式编辑才解锁字段并显示 Automation BLOCK 状态；
5. 新建布局默认得到左右两栏草稿，四种预设在画布中对应 1 / 2 / 3 / 4 个 Zone；
6. Fresh Preview 显示目标点 / 轨迹与移动 / 保持 / 目标屏摘要；
7. “详细影响”仍可看到 Observation 与精确条目，默认不抢占主视觉；
8. 设置页出现桌面分区框外观卡，Light / Standard / Strong 可切换；
9. 边框粗细、边框透明度、填充透明度、圆角四个参数能实时改变右侧小预览；
10. 点击“在桌面上预览”后，主屏显示两栏示例圆角框，普通应用可盖住它，鼠标穿透；
11. 桌面预览开启时拖动参数，示例框稳定刷新，不出现卡死 / 闪退 / 全局置顶；
12. “取消”恢复已保存参数并关闭临时桌面预览；“应用”后 settings.json 出现 overlayAppearance；
13. 重启后设置页恢复已保存外观；不先访问设置页，直接从布局页打开 Overlay 也使用已保存外观；
14. Layout Overlay 与 Settings Desktop Preview 均不会改变任何真实桌面图标位置；
15. 新布局 V1 → 编辑保存 V2、ActiveLayoutTemplateId 持久化、单实例 / 托盘 / 显式退出无回归；
16. 全程不存在 Writer / unattended desktop movement。
