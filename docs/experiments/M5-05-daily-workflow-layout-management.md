# M5-05 Daily Workflow + Layout Management

## 目标

把 M2/M4 已冻结的布局模板、规则、只读桌面观察和预览规划能力接入 WinUI 产品壳，形成可以日常使用的布局管理工作流。

M5-05 只解决“创建 / 编辑元数据 / 预览影响 / 原子保存 / 选择当前布局 / Overlay 展示偏好”。本阶段仍不执行真实桌面移动。

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

布局页为三栏工作台：

```text
布局列表 | 可视化桌面画布 | 轻量属性
```

产品文案遵循“主界面讲发生了什么，详细信息再讲系统内部为什么”的原则，避免将 Policy、Observation、Writer 等开发合同作为常驻说明墙。

产品交互原则：**Selection = passive preview/detail**。选择布局立即展示 Zone 与主要属性；显式“编辑”只用于真正修改布局元数据并进入 Layout Editing / Automation BLOCK。显示偏好（如 Zone 颜色）不借用布局编辑授权，但仍要求显式“应用颜色”才持久化。

AI 助手先建立一级导航与产品空状态；真实对话能力后续接入时，AI 生成的方案必须产出同一个 `LayoutDraft` 合同，再进入布局工作台继续可视化编辑，而不是另建一套 AI-only 布局模型。

## Desktop Zone Overlay

### 安全窗口边界

真实 Windows Gate 曾发现首版 Overlay 使用 `WS_EX_TOPMOST + HWND_TOPMOST`，导致分区线覆盖所有普通程序并出现卡死/关闭。该实现已经废弃。

当前 Overlay：

- 由 Windows Shell desktop window owning；
- non-topmost；
- click-through + no-activate；
- 普通应用窗口自然覆盖它；
- Shell desktop 不可用时 fail closed；
- native WndProc 绘制异常隔离；
- x64 `PAINTSTRUCT` 使用正确 blittable 布局；
- 不读取或写入图标位置；
- 不引用 Writer。

用户已在真实 Windows 重测确认 Z-order / 鼠标穿透 / 进程稳定性通过。

### 外观设置

Overlay 从“调试细线”提升为可配置圆角分区框。全局外观由 `DesktopOverlayAppearanceSettings` 描述：

- Light：2px / 34% border / 3.5% fill / 14px radius；
- Standard：3px / 52% border / 7.5% fill / 16px radius；
- Strong：4px / 74% border / 14% fill / 18px radius；
- Custom：高级参数或主题颜色变化后进入自定义；
- 默认主题色 `#0078D4`。

高级范围：边框 1–8px、边框透明度 10–100%、填充透明度 0–35%、圆角 0–32px、24-bit RGB 主题色。

Renderer 每个目标显示器最多固定两个轻量 Shell-owned 窗口：

```text
Chrome layer → 圆角边框 + Zone label
Fill layer   → 圆角填充
```

两个 layer 分别使用 BorderOpacity / FillOpacity，因此透明度真正独立。颜色变化不增加窗口数量。

设置页使用 PowerToys 风格双级预览：

1. 内嵌小桌面实时预览；
2. ColorPicker 可视化选色，Hex 只是辅助显示；
3. 显式“在桌面上预览”显示安全示例 Zone；
4. Slider / ColorPicker 调整不会立即写 settings.json；
5. “应用”才持久化，“取消”恢复上次已保存值；
6. 真实桌面预览使用约 120ms 防抖，避免拖动时频繁重建 native window。

### 逐 Zone 颜色

不同 Zone 可以覆盖全局主题色，但颜色**不进入 `DesktopLayoutZone` 领域模型**。该领域对象继续只承载几何、排序、放置和容量语义。

逐 Zone 颜色由独立 `DesktopOverlayZoneColorPreferences` 持有：

```text
Global ZoneColorRgb
       ↓
Zone 无 override → 继承全局
Zone 有 override → 使用自己的 RGB
```

产品行为：

- 布局页右侧 Zone 列表支持单选；
- 选择保存好的 Zone 后可以开启“单独颜色”并通过 ColorPicker 选色；
- 中央布局画布立即按有效 Zone 颜色刷新；
- 已打开 Desktop Overlay 时约 120ms 后刷新真实桌面颜色；
- 用户必须显式点击“应用颜色”才写入产品设置；
- “还原”恢复上次已应用颜色；
- 存在未应用颜色时阻止切换布局 / 新建 / 替换分区 / 保存布局，避免静默丢失；
- 新布局先保存得到稳定 ZoneId 后，才能保存逐 Zone 颜色；
- 全局厚度、透明度、圆角仍由所有 Zone 共享，本阶段只允许逐 Zone 覆盖颜色。

逐 Zone 颜色作为 formatVersion=1 的可选 `overlayZoneColors` 持久化；旧配置缺少字段时为空集合。MainWindow 的 Automation / AI 设置保存以及 ActiveLayout 更新都保留该偏好，避免跨页旧快照覆盖。

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

## Pending Queue 产品边界

M5-05 保留 Pending Queue 为只读。

M5-02 Confirm Once / Confirm & Remember 会删除 Pending 项并返回仅供后续 Fresh Execution Pipeline 消费的一次性 `DirectUserRequest` 授权。当前产品壳还没有完整 Fresh Execution Pipeline；提前接确认按钮会造成“项目消失但批准动作未执行”的错误产品语义，因此不提前消费该结果。

## 安全边界

- Layout Editing → Automation Policy BLOCK；
- Preview 只读，不产生 WriterCommand；
- Preview 每次从新鲜 DesktopObservation 生成；
- Desktop Overlay 只显示 Zone 元数据与 presentation preferences；
- 外观/颜色设置不授予任何执行权限；
- “保存”只保存 Template / Rules 元数据；
- “使用”只保存 ActiveLayoutTemplateId；
- 产品壳不构造 `DesktopShellIconPositionWriter`；
- 不打开 production unattended gate；
- 不修改 Recovery safety semantics；
- 不修改共享 SQLite schema；
- 不移动任何真实桌面图标。

## 当前验证状态

最近一次真正启动并完整执行的 CI 为 Actions #344 / HEAD `de1ce7d...`：1841 / 1841 PASS，Debug / Release PASS。之后 GitHub Actions 受账户 billing / spending-limit 外部 Gate 阻塞，runner 未启动，因此最新 Overlay Appearance / Color / per-Zone Color 代码由本机 Debug build + 真实 Windows UI Gate 临时承担验证，待 Actions 恢复后补完整 CI。
