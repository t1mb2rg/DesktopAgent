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

Preview 展示：

- 目标显示器；
- 输入 / 目标屏 / 屏外图标数；
- 需移动 / 已在目标；
- Excluded / Pinned；
- Planning 是否完整；
- 每项当前位置与预期目标位置；
- 本次 ObservationId 与观察时间。

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

## 安全边界

- Layout Editing → Automation Policy BLOCK；
- Preview 只读，不产生 WriterCommand；
- Preview 每次从新鲜 DesktopObservation 生成；
- “原子保存草稿”只保存 Template / Rules 元数据；
- “设为当前”只保存 ActiveLayoutTemplateId；
- 产品壳不构造 `DesktopShellIconPositionWriter`；
- 不打开 production unattended gate；
- 不修改 Recovery safety semantics；
- 不移动任何真实桌面图标。

## Physical Windows Gate

CI 与 Scope Audit 通过后，在真实 Windows 11 x64 验证：

1. 布局页可正常加载，原有四页导航无回归；
2. 新建布局默认得到左右两栏内存草稿，并显示 `Automation Policy: BLOCK`；
3. 可切换常用分区预设，Zone 列表正确刷新；
4. 点击“重新观察并预览影响”可读取当前真实桌面并显示 Observation / 影响统计；
5. 预览前后桌面图标位置完全不变；
6. 新布局原子保存成功并在列表中显示 V1；
7. 重新加载、改名并保存后版本推进为 V2；
8. “设为当前”后列表显示“当前”，settings.json 持久化 activeLayoutTemplateId；
9. 重启后模板与当前布局选择仍保留；
10. 单实例、关闭到托盘、托盘恢复、显式退出无回归；
11. 全程不存在 Writer / unattended desktop movement。
