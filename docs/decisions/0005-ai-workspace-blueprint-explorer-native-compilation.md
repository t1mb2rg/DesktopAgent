# ADR-0005：AI Workspace Blueprint 与 Explorer 原生编译

- 状态：已接受
- 日期：2026-08-27
- 取代：ADR-0004《AI 获得完整逻辑工作区规划权，但不获得 Writer Authority》

## 背景

ADR-0004 解决了一个真实问题：不能让本地转换器在 AI 与 Preview 之间静默脑补几何、复制 Zone 或改变用户意图。因此它把 AI 的规划权推进到完整逻辑工作区终态，包括 Zone 网格边界和每个项目的精确逻辑格位。

M5-07.7 的真实 Windows Physical Gate 随后连续暴露出一组同源问题：

- 多显示器上的 Explorer Grid Phase 不能简单视为全局稳定逻辑网格；
- 真实图标可能位于自由摆放位置、边缘残格或非规则网格位置；
- 为满足 `CurrentCell` 模型而产生了 tail projection、phase fallback、occupied-cells 等补偿逻辑；
- Semantic Understanding 与 Exact Logical Planner 同时拥有 Zone 规划权，导致设计 authority 重叠；
- 第二次 Planner 需要处理约 130 个项目的 Zone、屏幕、状态和 exact cell，真实机出现 300 秒级超时；
- Semantic Understanding 已形成多 Zone 语义，但第二次 Planner 可以合法重新发明为单一大 Zone；
- 逐项 exact-cell 输出使 AI 承担了本应由 Windows 几何适配层处理的机械 packing 工作。

这些问题表明，核心错误不是某一个 Prompt 或 Validator 缺口，而是把三种不同的权威混在一起：

1. Windows Explorer 对真实桌面几何的权威；
2. AI 对用户可感知桌面设计的权威；
3. 本地代码对确定性、安全施工的权威。

因此，DesktopAgent 不再把“AI 直接输出完整逻辑网格终态”作为目标架构，而改用 Workspace Blueprint + Explorer-native deterministic compilation。

## 核心决策

目标链路为：

```text
Conversation
    ↓
Semantic Understanding
    ↓  用户明确请求生成/修改布局方案
Workspace Design
    ↓
AI Workspace Blueprint Revision
    ↓
Blueprint Applicability / Validation
    ↓
Explorer Geometry Adapter
    ↓
Deterministic Workspace Compiler
    ↓
Semantic Equivalence Check
    ↓
Exact Workspace Plan + Realization Certificate
    ↓
Frozen Workspace Preview
    ↓
User Confirm
    ↓
Fresh Observation
    ↓
Same-result check / faithful recompile
    ↓
Workspace Execution Session
    ↓
Safety Snapshot / Recovery Checkpoint
    ↓
Primitive Writer
    ↓
Independent Verification / Recovery
```

核心原则：

> Explorer owns geometry. Tessel owns intent.

> AI owns the Workspace Blueprint. Local code may compile it, but may not redesign it.

> 用户确认的是 AI Blueprint 以及自己实际看过的精确编译结果。

## AR-01.1：Explorer 是几何权威

真实 Windows 桌面允许是不规则的。当前图标无法可靠映射到某个规则逻辑格，不应导致整个桌面 Observation、Semantic Understanding 或 Workspace Design 失败。

因此：

- `DesktopObservation` 忠实记录 Windows 当前事实；
- 当前项目可以处于非网格、自由摆放或边缘位置；
- 规则 Cell/Slot 不再是解释现实的前置真理；
- Explorer 网格、spacing、phase、安全可写位置属于 Windows Geometry 层；
- Tessel 不要求现实桌面先符合自己重建的 Logical Grid。

现有 Grid Phase、spacing、负坐标和边缘位置相关能力应迁入/复用于 Explorer Geometry Adapter，而不是继续向 Semantic/Blueprint 领域泄漏。

## AR-01.2：Conversation 与 Workspace Design 分离

普通聊天、语义理解与正式布局设计不是同一个 authority 状态。

Semantic Understanding 只回答“桌面上这些东西是什么”：

- Categories；
- Item → Category；
- confidence；
- ambiguity / questions；
- Provider-safe semantic summary。

Semantic Understanding 不拥有：

- Zone；
- target display；
- Reserved Region；
- Keep / Move / Exclude policy；
- layout geometry。

只有用户明确请求“生成布局方案”“帮我设计/整理桌面”或等价的显式产品动作后，系统才进入 Workspace Design。

## AR-01.3：AI 拥有真正的桌面设计权

AI 不能退化为只输出几个提示词，再由本地代码自行设计最终布局。

AI Workspace Designer 拥有用户可感知的设计决定，包括：

- 整个多显示器 Workspace 的结构；
- Zone 的存在、语义、目标显示器角色；
- Zone 的相对位置、比例和关系；
- Category → Zone 规则；
- 少量 Item Override；
- Keep / Exclude；
- Reserved Region；
- anchor / priority 等显式视觉意图。

本地代码不得替 AI 改屏幕、换 Zone、调整设计关系或创造新的布局政策。

## AR-01.4：多显示器布局是一张 Workspace Blueprint

一个跨屏桌面设计是一个整体 Workspace Blueprint，而不是多份彼此独立的 per-display Draft。

跨屏移动天然同时影响源屏和目标屏。因此：

- Preview 以整个 Workspace 为单位；
- 用户确认整个 Workspace；
- Snapshot、Recovery、Verification 也以 Workspace 为事务语义；
- Writer 仍可以逐项执行，但不改变 Workspace-level transaction identity。

旧单屏 Layout 仍可以保持 per-display 语义，不能为了复用旧业务模型而把新 AI Workspace 再拆回多份 Draft。

## AR-01.5：Blueprint 使用相对设计空间，而不是 Explorer Cell

Blueprint 与 Windows 分辨率、物理像素和 Explorer logical cell 解耦。

Blueprint 可以表达：

- display role/reference；
- Zone 相对区域，例如 left/right/top/bottom/center；
- width/height ratio；
- Zone 间 `left_of`、`right_of`、`above`、`below` 等关系；
- Reserved Region；
- anchor；
- Category → Zone 与 Item Override。

例如 AI 可以设计“Tools 占下方副屏左侧约 35%”，但不输出 `(column=8,row=3)`。

Explorer Geometry Adapter 再把这些设计约束映射到当前 Windows 实际合法 slot。

## AR-01.6：Frozen Preview 同时绑定设计与实现

Frozen Workspace Preview 必须绑定：

```text
Blueprint Revision
+
Compiled Exact Workspace Plan
+
Realization Certificate
+
Source Observation identity
```

用户确认的是：

> 我接受这一版 AI Blueprint，以及它在当前 Windows 环境下我实际看过的这次精确实现。

Fresh Check 后若需要重新编译，并且精确可见结果发生变化，即使新结果仍与 Blueprint 语义等价，也必须生成新的 Frozen Preview 并重新确认。

## AR-01.7：Workspace 是一个用户事务，Windows 执行不是原子事务

用户级事务是整个 Workspace，但 Windows Shell 不提供真正原子性的多图标事务。

因此执行必须持久化：

- confirmed Blueprint revision identity；
- confirmed exact plan identity；
- starting observation / safety facts；
- intended final state；
- recovery checkpoint。

Recovery 不依赖“历史上肯定执行到第几步”的猜测，而重新读取当前事实并与已持久化意图比较。

原则：

> current verifiable facts + persisted intent are authoritative; execution history alone is not.

Recovery 无权重新设计 Blueprint。

## AR-01.8：Blueprint 默认使用 Category → Zone + 少量 Item Override

Workspace Designer 默认不逐项输出所有 Item 的目标 Zone，更不逐项输出 exact cell。

Semantic Understanding 已经提供：

```text
Item → Category
```

Blueprint 主要提供：

```text
Category → Zone
```

两者在本地可确定性展开为：

```text
Item → Category → Zone
```

只有真正的例外使用 Item Override，例如特殊 anchor、Keep、Exclude 或明确跨类别放置。

Workspace Designer 默认消费 Category summary、count、design-critical exceptions 与 workspace topology，而不是再次吞入完整 130+ item 清单。这是性能与可扩展性的核心设计，而不是仅压缩 JSON key。

## AR-01.9：不确定性在 Design Gate 前处理

Semantic Understanding 允许保留 uncertainty。

- 不会 materially change Blueprint 的轻微不确定性，可进入 `Uncertain/Inbox` 或等价兜底类别；
- 会改变布局设计、保护边界或用户目标的重要歧义，必须在进入 Workspace Design 前询问用户；
- 不允许产品一边显示“需要确认”，一边继续生成正式 Blueprint。

## AR-01.10：Blueprint 是长期、版本化设计资产

Workspace Blueprint 是可持久化、可复用的设计资产；Compiled Exact Plan 是绑定某次 Observation 和 Explorer Geometry 的临时施工结果。

保存 Blueprint 不授予任何执行权限。

Blueprint revision：

- 不可变；
- 有稳定 Blueprint identity；
- 新设计产生新 revision；
- 可以记录 parent revision / revision reason；
- Preview 和 Execution 必须明确绑定具体 revision。

如果当前桌面仍可忠实实现旧 Blueprint，则复用旧 revision 并重新编译；如果无法忠实实现，则返回 Workspace Design，由 AI 创建新 revision。

## AR-01.11：Compiler 只有施工权

Deterministic Workspace Compiler 可以：

- 把相对区域量化到 Explorer-native 几何；
- 展开 Category → Zone 为具体 Item → Zone；
- 按 Blueprint 指定的顺序/anchor 排列；
- pack 到合法 slot；
- 避开固定系统项和不可用 slot；
- 生成精确 target position；
- 生成确定性的执行输入。

Compiler 不得：

- 分类项目；
- 改 Zone 所在显示器；
- 改 Zone 相对关系；
- 为了容量主动扩大/缩小到改变设计语义；
- 把项目塞进另一个 Zone；
- 擅自 Keep / Exclude；
- 新增 Overflow Zone；
- 删除 Zone / Reserved Region；
- 使用审美启发式重新设计。

同样的 Blueprint + Semantic Snapshot + Explorer Geometry 必须产生同样的精确计划。

如果无法忠实编译：FAIL，不得 improvise。

## AR-01.12：Compile Failure 必须结构化返回 AI

Compile failure 不是流程终点，而是 Workspace Designer 的受控反馈环。

Compiler 只返回 Provider-safe 的现场约束事实，例如：

- `ZoneCapacityInsufficient`；
- required / available slot count；
- fixed obstacle count；
- unavailable display role；
- mutually unsatisfiable constraints；
- nearest faithful quantized geometry；
- minimum geometry required for capacity。

Compiler 不告诉 AI“应该如何改设计”。AI 根据事实创建新的 immutable Blueprint revision。

AI redesign retry 必须有限，超过预算则进入 `DesignUnresolved` 并要求用户调整目标或确认关键取舍。

## AR-01.13：Semantic Equivalence 必须本地确定性验证

Semantic Equivalence 不能再次交给 AI，也不能靠“看起来差不多”。

Hard Semantics 必须完全保持：

- Zone existence；
- display ownership；
- Zone relative topology；
- Category → Zone；
- Item Override；
- Keep / Exclude；
- Reserved Region；
- explicit anchor semantics。

Quantizable Semantics 只允许 Explorer-native 离散化不可避免的最小误差，例如 35% 区域映射到 24 列时选择最近合法边界。

Compiler 不得把“量化”当作扩大容量或重新设计的许可证。

每次成功编译必须产生 `Realization Certificate`，证明 Blueprint 的 hard semantics 与 quantizable semantics 如何被忠实实现。

## AR-01.14：产品状态机必须显式分权

产品状态至少区分：

```text
Conversation
Understanding
DesignRequested
WorkspaceDesign
BlueprintReady
Compile
FrozenPreview
UserConfirmed
FreshCheck
Execution
Verification / Recovery
```

自然语言可以请求状态转换，但不能静默折叠权限边界。

特别是：

- Conversation ≠ Design；
- Design ≠ Preview；
- Preview ≠ Confirmation；
- Confirmation ≠ Execution。

Frozen Preview 期间继续聊天不能自动修改当前 Preview；修改需求必须创建新的 Blueprint revision 并重新 Compile / Preview。

自然语言中的“挺好”“不错”不能被猜成 Writer 授权，真实 Apply 必须保持明确的产品级确认动作。

## AR-01.15：旧单屏 Layout 与新 AI Workspace 是两条产品路径

Legacy single-display Layout 保留现有产品和事务语义。

新 AI Workspace 不为了复用旧 Coordinator 而编译成多份 `DesktopLayoutEditDraft`。

两条路径可以共享：

- Desktop Observation primitives；
- Display topology；
- Shell icon reader；
- Explorer geometry primitives；
- primitive position writer；
- persistence primitives；
- snapshot / verification / fail-closed recovery mechanics。

但不能共享错误的业务 scope。

原则：

> reuse mechanics, not the wrong domain model.

`DesktopUnderstandingMultiDisplayWorkspaceCompiler` 可以继续作为 legacy / compatibility path，但新 Workspace Designer 不依赖它。

## 新的 Authority 划分

### Semantic Understanding

只理解，不设计。

### AI Workspace Designer

拥有用户可感知的 Workspace Blueprint 设计权，但没有 Windows geometry、Candidate、Recovery 或 Writer authority。

### Explorer Geometry Adapter

只描述/推导当前 Windows Explorer 可安全使用的真实几何和 slot，不解释用户意图。

### Workspace Compiler

只忠实编译 Blueprint，不拥有语义或设计权。

### Semantic Equivalence Checker

本地、确定性地验证 Exact Plan 是否忠实实现 Blueprint。

### Frozen Preview

绑定 Blueprint revision 与精确实现，是用户确认对象，不是 Writer authority。

### Workspace Execution / Recovery

执行用户已确认的精确实现；任何无法继续忠实完成的情况必须 fail-closed 或回到 re-preview / redesign，不得改变设计。

### Primitive Writer

继续维持最小、无意图的移动原语。

## 与 ADR-0004 的关系

ADR-0004 的以下原则继续有效：

- AI 不获得 Writer authority；
- Preview 必须忠实于设计，不能由本地层静默脑补；
- fresh observation、safety snapshot、recovery checkpoint、independent verification 继续存在；
- 多显示器布局应以 Workspace 为事务语义；
- Writer 保持最小原语。

ADR-0004 的以下决策被本 ADR 取代：

- AI 直接读取/依赖完整 Explorer logical grid；
- AI 直接输出 Zone exact grid bounds；
- AI 为每个 item 输出 exact logical cell；
- `DesktopObservedLogicalWorkspace` 作为 AI 规划的必经前置模型；
- `DesktopDesiredWorkspacePlan` 作为 AI 直接提交的完整 exact-cell 终态；
- 第二次 AI Exact Logical Planner 作为 Workspace Design 的核心。

## 当前 M5-07.7 代码的处理原则

### KEEP / 下沉为基础设施

- Windows Desktop Observation；
- Display topology；
- Shell reader；
- primitive Writer；
- Grid Phase / spacing 等真实 Explorer 几何发现；
- Safety Snapshot；
- independent verification；
- recovery fail-closed 原则；
- 多屏 Topology Canvas；
- Preview 大量迁移线可读性优化；
- Frozen Preview 生命周期保护原则；
- SystemItem 不可移动 invariant。

### REWRITE

- AI Logical Workspace planning 中段；
- exact-cell Planner contract；
- `DesktopObservedLogicalWorkspace` 的领域职责；
- `DesktopDesiredWorkspacePlan` 的 AI-authoritative exact-cell 模型；
- Frozen Preview 数据模型，使其绑定 Blueprint + Exact Plan + Certificate；
- 新 Workspace-level Candidate / Recovery scope。

### LEGACY / COMPATIBILITY

- `DesktopUnderstandingMultiDisplayWorkspaceCompiler`；
- per-display `DesktopLayoutEditDraft` 产品路径；
- 旧单屏 Direct Apply / Recovery 语义。

新 AI Workspace 不应继续扩大对这些业务模型的依赖。

## 实验性提交边界

M5-07.7 分支从 `1b4c91ebd97d3095593bc44e1428d0a074db5839` 到 `10b57bc398af75d3605d00b080e21a4d43b008a8` 的 5 个提交，均属于 Architecture Review 前为 exact-cell Planner 增加 semantic continuity 的实验性补丁。

这些提交不作为 ADR-0005 新架构的基础。后续重构应整体撤销或重写其方向，而不是继续在其上叠加补丁。

## 非目标

本 ADR 不：

- 授权 AI 直接执行 Windows 写入；
- 允许 Compiler / Recovery 擅自修改 Blueprint；
- 为了复用旧单屏业务模型而牺牲 Workspace 事务语义；
- 要求当前现实桌面先符合一套 Tessel 自建网格；
- 允许保存 Blueprint 自动等价于未来执行授权；
- 在本 ADR 中一次性删除 legacy Layout 能力。

## 后果

主要收益：

- AI 仍真正设计桌面，但不再模拟 Explorer；
- Explorer 几何异常不会再污染语义理解和设计层；
- Workspace Designer 的输入/输出规模从 130+ item exact placement 缩小到 category summaries、Zone design 与少量 overrides；
- Compiler 可本地确定性、快速、可测试；
- Preview 可以证明“AI 设计”和“Windows 实际落成”之间的忠实关系；
- Workspace-level Recovery 与跨屏设计语义一致；
- 失败能够结构化返回 AI，而不是通过本地静默修复扩大 authority。

主要代价：

- 需要新增 Blueprint、Geometry Adapter、Compiler、Equivalence、Certificate 等领域模型；
- 当前 Logical Workspace 中段需要较大重写；
- Workspace-level Execution / Recovery 不能简单复用现有 per-display 业务聚合根；
- 需要新的真实 Windows Gate 验证 Explorer-native compilation。

本 ADR 冻结的是架构边界。具体类名、schema 细节和分阶段迁移顺序由 M5-07.7 重构计划与后续代码 Gate 决定。
