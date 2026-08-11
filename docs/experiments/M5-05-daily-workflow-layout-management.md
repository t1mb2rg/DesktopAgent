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

AI 助手先建立一级导航与产品空状态；真实对话能力后续接入时，AI 生成的方案必须产出同一个 `LayoutDraft` 合同，再进入布局工作台继续可视化编辑，而不是另建一套 AI-only 布局模型。

## Desktop Zone Overlay MVP

M5-05 增加会话级 Desktop Zone 线框预览：

- 通过 `DesktopZoneOverlayHost` 在目标显示器 WorkArea 上绘制 Zone 边界和名称；
- Overlay 使用独立、鼠标穿透、不可激活的 Win32 顶层辅助窗口；
- 主窗口隐藏到托盘时，用户主动开启的线框可以继续显示；
- 本阶段 Overlay 不写入永久设置，先用于验证视觉效果与日常可接受度；
- 新建 / 重新加载 / 替换 Zone / 取消编辑时主动关闭旧 Overlay，避免显示与草稿不一致；
- Overlay 只消费 DisplayDescriptor + Zone 元数据，不读取或写入桌面图标位置；
- `DesktopZoneOverlayHost` 不引用 Writer，也不暴露 Move API。

后续若视觉体验通过，再评估持久化三态：隐藏 / 始终显示 / 仅整理时显示，以及线条强度、Zone 名称和图标开关。

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
- Desktop Overlay 只显示 Zone 元数据，鼠标穿透，不拥有 Writer；
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
3. 布局页为三栏工作台，不再使用纵向说明墙；
4. 新建布局默认得到左右两栏草稿，中央画布直接显示两个 Zone；
5. 单区 / 左右两栏 / 三栏 / 2×2 四宫格切换后，画布同步变成 1 / 2 / 3 / 4 个 Zone；
6. “预览”重新观察真实桌面，画布显示目标点 / 轨迹，移动 / 保持 / 目标屏摘要更新；
7. “详细影响”仍可看到 Observation 与精确条目，且默认不抢占主视觉；
8. “桌面线框”在真实目标显示器显示 Zone 边界和名称，鼠标可穿透，点击桌面图标不受阻；
9. 主窗口隐藏到托盘时，已开启的会话级线框仍可保留；再次点击可隐藏；
10. 预览和 Overlay 前后真实桌面图标位置完全不变；
11. 新布局保存显示 V1；重新加载改名保存推进 V2；
12. “使用”后列表显示“当前”，settings.json 持久化 activeLayoutTemplateId；
13. 重启后模板和当前布局选择仍保留；Overlay 本阶段不要求跨重启保留；
14. 单实例、关闭到托盘、托盘恢复、显式退出无回归；
15. 全程不存在 Writer / unattended desktop movement。
