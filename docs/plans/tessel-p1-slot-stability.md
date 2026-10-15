# P-1 槽位稳定性候选：收口审阅基线

状态：**Core 候选已实现，默认未启用；不得视为产品已切换或真实 Writer 已验收**。

## 已有问题与目标

2026-10-15 真机上监督布局为 0 项，向前插入一个新的桌面图标后自动建议 17 项。复现测试证明旧分配器按 `Icon.Ordinal` 填满顺序槽位，新项目插队导致后续全部顺移。

P-1 的目标不是让数字变小，而是证明：**不违反明确规则和几何/容量约束时，允许已有项目继续占着原生槽位**，从而降低不必要的实际桌面写入数量。

## 候选合同

1. 明确 Exclude / Pin 是固定障碍，原样保留；不得占用其图标矩形。
2. ZoneOverride 先于 Default 分配。Override 已在自己的合法槽位中可以留下；发生槽位竞争时按原 `Icon.Ordinal` 仲裁。
3. Default 按 `Zone.SortOrder` 和剩余容量分桶，**不为了保持原位跳过用户定义的更高优先级分区**。在其既定分区内合法落点优先占用，其他项目再走可用槽位列表。
4. 是否“合法槽位”只按目标屏归属、PlacementOrigin 相位、spacing、理论槽位范围、Zone 完整包含以及固定障碍判定；容量仍限制已分配**数量**而非 SlotIndex 数值。
5. 候选通过 `preserveExistingGridSlots=true` 显式启用；默认参数仍为 false，原顺序语义不变。仅暴露纯 Core 预览能力，不增加新的 Writer 或自动授权。

## 现有测试与需补充的 Gate

- 新项目插在 Ordinal 最前、原先图标都在合法格位：旧策略全量位移；新候选保持全部原位。
- 新项目插在最前但本身不在应有格位：只分配该项目至空格，原合法项目不动。
- Override 抢占相同格位：Override 留下，Default 让位。
- 不属于目标屏且没有明确跨屏授权的图标不参与本屏排布。
- **已补**：Pin 固定障碍、Zone.MaxItemCount、两个 Zone 的容量与顺序、非原生偏移、容量耗尽、显式跨屏迁入授权、负坐标以及相同输入下多次规划确定性。
- **仍需**：Exclude 障碍、高密度多区实例、冲突/重叠 Zone 反例，以及真实 Windows 全屏物理验收。
- 必须运行整个 Release tests，并确认旧 Legacy Plan 不受影响。
- 产品正式切换前，统一由 `DesktopLayoutRuleAwarePreviewPlanner.Plan` 进入 stable 模式，确保监督预览和无人值守建议使用**相同配置和同一算法**，不得出现“面板 0 / 引擎 17”的策略分歧。
- 真机 Physical Gate：不修改已有数据基线，先独立快照→小幅漂移→监督预览→安全应用→独立回读→撤销；不能放宽风险预算。

## 暂不自动裁决的语义分叉：跨分区稳定性

目前候选的“保持原位”只发生在**已经由现有规则决定归属的同一个 Zone 内**。默认图标仍先按 `Icon.Ordinal` 配合 `Zone.SortOrder` / `MaxItemCount` 分配到 Zone。新增项目插入队首、且某个 Zone 容量已满时，即使两个旧图标都在不同合法分区，仍可能因**默认分区归属变化**而移动。新增双分区测试明确记录了此行为。

这是一个必须先作产品裁决的取舍，而不是已修好的问题：
- **保守候选（当前实现）**：尊重既有顺序分桶，不暗改旧模板语义；只减少同分区内的无谓位置变化。
- **更强稳定性（待单独裁决）**：先保留所有仍位于合法 Zone 的默认图标，再按优先级填剩余容量。它更少写桌面，但可能改变“前面的项目优先占满前面的 Zone”这一既有规则。

在裁决前不擅自把 P-1 作为产品默认策略，不把 Core 候选接入自动 Writer；比较应使用同一批输入并列输出旧/新 Plan 及每项变动原因。

## Functional Review / Architecture Review（2026-10-15）

**已核实的代码边界：**

- `DesktopLayoutRuleAwarePreviewPlanner.Plan(... preserveExistingGridSlots=true)` 只调用纯 Core PlacementPlanner；没有增加新的 Shell Writer、Confirmation、Restore、授权或自动执行入口。默认值仍是 `false`。
- `DesktopLayoutRuleAwarePlacementPlan` 仍验证 Override / Default 的 Rule/Icon 引用身份、TargetPosition 唯一和固定障碍不重叠；`DesktopLayoutRuleAwarePreviewPlan` 逐项验证 TargetPosition / SlotIndex 与 PlacementPlan 完全相同。
- `DesktopLayoutRuleAwareTargetSnapshotFactory.Create` 只在 `IsComplete` 时允许生成可执行目标。已提交从**高序号非连续合法槽位 → Preview → TargetSnapshot** 的综合测试；Pin / Exclude 不进入执行目标，OutOfScope 原位置及显示器保持不变。它没有要求槽位必须从零连续。
- `DesktopLayoutTemplate` 已验证归一化 Zone 不重叠、SortOrder 唯一；`ResolvedDesktopLayoutTemplate` 又验证物理 Zone 不重叠及容量，所以不以“任意重叠区域”作为受支持输入。
- 新的“零可用槽位”场景必须保持 `Unassigned`，随后目标快照工厂拒绝创建写入目标，不能回退到模糊坐标或猜测写入。

**仍然阻断产品正式启用的两项条件：**

1. **语义裁决**：跨 Zone 的“严格顺序分桶”与“最大稳定保留”只能选一种明确的产品合同，不能由监督执行/自动建议各自暗中选择。
2. **物理一致性**：Core 合法槽位不等于已验证当前 Explorer 可执行落点。必须用实测网格事实和当次物理回读证明：Phase/Spacing、Pin/Exclude、跨屏 Scope、容量和 Writer 落点在同一次 Windows 场景中严格一致；不允许为了变绿而改大无人值守风险预算。

**Gate 判定：** 当前可进入进一步 review/只读对比演示，**不能标记 Physical Gate 通过，也不能合并成默认执行策略**。

## 产品路径覆盖清单（避免误报）

**传统模板链**（当前 P-1 适用）：`DesktopLayoutImpactPreviewService`（用户监督预览）与 `DesktopLayoutSuggestionBuilder`（自动建议）调用同一个 `DesktopLayoutRuleAwarePreviewPlanner.Plan`。这解释了 §31 的“增加一个图标触发 N 次重排”。`preserveExistingGridSlots` 目前仅是 Core 可选参数；两条**产品调用路径还没有共同启用它**。

**AI Workspace 链**（本 PR 未修改）：`DesktopWorkspaceCompiler.Compile` 通过 `Category→Zone`、`ItemOverride` 和本机实测 `ExplorerNativeSlot` 做确定性分配。其当前代码先按 `StableId` 收集项目，再对未指定独立 anchor 的项目按 `StableId` 顺序依次绑定 `OrderSlots(...)` 的槽位；并**没有**引用本 PR 的稳定槽位候选。若新增项目影响 `StableId` 顺序，该链也需要单独评估“无谓挪动”风险，但尚未通过专项测试证实。

因此完成 P-1 前还须分清两个结论：
1. **模板 + 自动建议的一致性**：可由本 PR 候选加产品共同入口、预算与物理写入验收确认。
2. **AI Workspace 的稳定性**：必须走其独立的语义/实测原生槽位 Compiler 审阅与测试。不得把传统模板的候选开关直接套在 Workspace 上，也不得复用旧的 Frozen Preview 权限。

## 禁止事项

不得改变 `DesktopLayoutRuleAwarePlacementPlan` 已验证的引用身份与唯一坐标不变量；不得为减少写入而移动 Pin/Exclude、跨越用户未授权显示器、绕开图标恢复检查点或降低执行安全门；不因本 PR CI 通过就宣布自动整理完成。
