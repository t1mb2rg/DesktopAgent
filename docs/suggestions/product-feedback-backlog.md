# 产品建议清单（待实施）

> 用途：把使用产品过程中随时提出的零散建议/问题记在这里，等「统一实施」时一次性排期。
> 建立于 2026-10-15（用户提出：建议会比较零散，先记下来，之后统一做）。
> 记录方式：每条一行、编号连续；实施后回填「状态」与「落地方式」。

| 编号 | 日期 | 原始建议（保留原话） | 归类 | 影响面 | 状态 |
| --- | --- | --- | --- | --- | --- |
| F-1 | 2026-10-15 | 「我不确定首次启动是怎么样的，但是我觉得首次启动最好让用户能看到自己需要固定一次桌面网格」 | 体验 | 首次启动引导：把「校准/固定桌面网格」作为必做第一步摆到眼前（现入口为面板 `GridProbeButton`「校准桌面网格…」，用户 14:39 自己找到并跑过一次） | 待实施 |
| F-2 | 2026-10-15 | 「布局这里的设计阶段说了要在左侧选择一个要使用的布局之后选择上之后再点使用他还是会弹报错」 | 体验 + 可靠性 | 设计阶段选布局后点「使用」报「请选择布局 / 先在左侧选一个要使用的布局。」——机制已定位（见 F-2 记录），报错与真实原因相反 | 待实施（机制已定位） |
| F-3 | 2026-10-15 | 「我生成精确预览之后回来点应用又是这样」（附截图：执行阶段工作区预览 + 顶部警告 + 底部「一次性凭据已失效」） | 体验 + 可靠性 | 工作区精确预览的**一次性凭据**在应用被 fresh check 拒绝后不可用；若桌面与冻结预览的期望持续不一致，会进入「重新生成 → 应用 → 又被拒」的循环 | 待实施（机制部分定位，触发项待复现） |
| F-4 | 2026-10-15 | 「但是现在已经移动好了，他现在点不了是不是有点本末倒置了」 | 体验（发现性 + 一致性） | 只要存在一份冻结的工作区精确预览，「▣ 桌面线框」等**只读可视化**入口一起被禁用——桌面已经摆好、正想开线框核对的时候反而点不了 | 待实施（机制已定位） |
| F-5 | 2026-10-15 | 「我现在就想用我现在的这个布局，所有图标都在左侧屏幕的这个，我觉得这个是个ui设计bug，你怎么看，我生成的布局居然没法出现在左侧或者很复杂才能看见」 | 体验 + 信息架构 | 自己生成的工作区设计（跨屏意图设计）**只在「设计」阶段可见**：一旦切到「执行」，`WorkspaceLibrarySection` 整块被 Collapsed；而左侧「我的布局」是**布局模板**（单屏分区模板），永远不会包含工作区设计 → 用户"生成了却找不到、也没法复用" | 待实施（机制已定位） |

## F-2 定位记录（2026-10-15，只读代码定位，未改动）

**现象**：在设计阶段，左侧列表里点选了某个布局，再点「使用」，弹出错误
「请选择布局 · 先在左侧选一个要使用的布局。」（`src/DesktopAgent.WinUI/LayoutManagementView.xaml.cs:229-233`）。

**机制**（三处代码构成闭环）：

1. `src/DesktopAgent.WinUI/LayoutManagementView.Selection.cs:13` `LayoutTemplateList_SelectionChanged` 是点选布局的入口。
2. 同文件 `:21-30`：处于编辑态（`_isLayoutEditing` 且有草稿/工作区会话/冻结预览）时，会调用 `RestoreSelectionToCurrentDraft()` 并给出 InfoBar
   「正在编辑布局 · 先保存或取消当前编辑，再切换到其他布局。」——**信息级提示，不是错误**，很容易被忽略。
3. `src/DesktopAgent.WinUI/LayoutManagementView.Selection.cs:174-181` `RestoreSelectionToCurrentDraft()`：仅当 `_draft is { IsNew: false }` 且能在列表中找到同 `TemplateId` 时才还原选中项；否则 `SetSelectedTemplateWithoutNotification(null)`（`:183-194`）把 `SelectedItem` **静默置空**。
4. 用户再点「使用」→ `SetActiveLayout_Click` 读到的 `SelectedItem` 已是 `null` → 报「先在左侧选一个要使用的布局。」——**把责任推给用户，真实原因是"你正在编辑一个未保存的布局"**。

**绕过办法（已告诉用户）**：先在设计阶段「保存」或「取消编辑」退出编辑态，再选左侧另一个布局并点「使用」；或回到准备阶段设置当前布局。

**2026-10-15 15:5x 用户复现（同一条机制的另一副面孔）**：用户在左侧列表点布局「选不上、没有被选上的效果」——即 `Selection.cs:27` 的 `RestoreSelectionToCurrentDraft()` 静默清空选中项，只留一条信息级 InfoBar（很容易没看到）。触发条件确认为**编辑态**：`Selection.cs:21` 要求 `_isLayoutEditing` 为真才走这条分支，所以「点不上」= 你还在编辑布局。**出口**：编辑态下按钮区会出现「取消编辑」（`Selection.cs:156-157`：`CancelLayoutEditButton.Visibility = isEditing ? Visible : Collapsed`，同时「编辑」按钮文字变成「桌面编辑」）——点它退出编辑态，再点左侧布局即可正常选中。

**修复方向（待实施时定稿，未选）**：① 让「使用」按编辑态给出真实原因；② 无可还原项时保留用户点选、只以 InfoBar 说明切换被阻止；③ 把「设为当前」从 ListView 的 `SelectedItem` 解耦（行内提供按钮）。

## F-3 定位记录（2026-10-15，只读代码 + 截图证据，未改动）

**现象**（截图 15:00，执行阶段，工作区预览「AI 工作区设计 v2」：整体拓扑 · 3 块显示器 · 跨屏 98；移动 142 / 保持 2 / 目标屏 多屏工作区）：

- 顶部警告：「当前精确预览已不能安全应用 — Writer 未启动。5 个以上项目的当前位置、所属显示器或可移动状态已变化：1.xlsx、2024级学员认识实习工作手册.docx、3DMark、ae51db4d2cb9f5866c2554e7c0f6c288.720.jpg、balenaEtcher。请重新生成精确预览，请重新生成精确预览后再试。」
- 底部就绪条：「⚠ 还不能应用 · 精确预览的一次性凭据已失效 — 这份 Frozen Preview 只保留作审阅。回到 AI 助手重新生成精确预览后，应用入口才会重新开放。」（「应用这个布局」按钮为灰）

**代码真源**：

- `src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceApply.cs:62`：Workspace Apply 只接受「当前会话里尚未消费的 Frozen Preview」。
- 同文件 `:271-272`：InfoBar 文案 =「当前精确预览已不能安全应用」+「Writer 未启动。{WorkspaceExecutionDiagnostic(result)} 请重新生成精确预览后再试。」
- `src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceDynamicExecution.cs:38-39`：同一形状的诊断文案。
- `src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopApplyReadiness.cs:132-138`：`EvaluateFrozenWorkspace()` → `!HasFrozenWorkspaceApplyTicket` → 「还不能应用 · 精确预览的一次性凭据已失效」；`:146-151` 通过时按钮文案为「可以应用 · 精确预览尚未消费」。

**观察到的闭环**：应用一次被 fresh check 拒绝（fail-closed，桌面未被写）之后，凭据已不可用 → 必须回 AI 助手重新生成；若桌面状态与冻结预览的期望**持续**不一致（即那 5 项），就形成「重新生成 → 应用 → 又被拒」的循环——与用户「又是这样」一致。

**待查（实施时需一次带证据的复现）**：

1. 那 5 项为什么被判「位置/所属显示器/可移动状态已变化」：当前桌面观察 vs 冻结预览期望的**逐项 diff**（期望值、实际值、差异类别）。
2. 被拒绝的那次应用是否真的消费掉了凭据——若"拒绝也消费"，应改为拒绝不消费，或至少把文案从"已失效"改成"已用尽"。
3. 是否与桌面「自动排列图标」或这 5 个文件正处于下载/新建过程有关（需真机确认）。

**修复方向（未定稿，涉及安全语义，需用户定调）**：fresh check 失败时保留或自动重建凭据；把差异做成可展开的逐项清单；对"与本次布局无关的项发生变化"是否继续 fail-closed 需明确表态。

## F-4 定位记录（2026-10-15，只读代码定位，未改动）

**现象**：用户已把桌面图标手动摆好，想开「▣ 桌面线框」核对时按钮是灰的——「本末倒置」。

**机制**：

1. `src/DesktopAgent.WinUI/LayoutManagementView.Selection.cs:143-145`：`isAnyFrozenWorkspacePreview = (_logicalWorkspaceSession is not null) || (_workspaceFrozenPreview is not null)`。
2. 同文件 `:148-160`：用这一个布尔一刀切禁用/只读了一整排控件——名称框、目标屏、设备名、预设、应用预设、保存、编辑，以及 **`OverlayToggleButton`（线框）** 与 `PreviewLayoutButton`（预览）。
3. 冻结预览真正要守的是**写入**：应用入口的一次性凭据（`LayoutManagementView.Readiness.cs:49-50`、`LayoutManagementView.WorkspaceApply.cs:38-46`）。

**为什么本末倒置**：线框是**只读、鼠标穿透、不触碰图标**的可视化——`src/DesktopAgent.WinUI/LayoutManagementView.xaml.cs:313-352` 里它自己的提示文案就是「它只是一层鼠标穿透的辅助框，不会触碰桌面图标」。把"看"和"写"用同一把锁锁起来，会在"我已经摆好、只想核对一下"的场景正好挡住用户。

**补充事实（2026-10-15 15:4x，用户澄清，使问题更尖锐）**：用户桌上这版布局**是产品自己应用成功的**（工作区精确预览 → 应用，预览显示「移动 42」），不是手拖出来的。于是：

- 应用结束后的设计意图写在代码注释里：`src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceApply.cs:170`——「Frozen Preview **仍然留作只读审阅**，但当前 Apply ticket 已消费」（同一 `finally` 里把 `PreviewLayoutButton.IsEnabled = false;`，`:172`）。
- 但实现上**「留作只读审阅」做不到**：`_workspaceFrozenPreview` 不为 null（`WorkspaceApply.cs:132` 只清 `_workspaceFrozenPreviewApplyPreviewId`，不清 `_workspaceFrozenPreview`），于是 `Selection.cs:144` 的 `isWorkspaceFrozenPreview` 继续为 true → `:154` 继续禁用「▣ 桌面线框」。
- 结果：用户刚让产品把 42 项摆好，**最想核对"到底摆成了什么样"的那一刻，核对工具正好被锁**。这既说明 F-4 的实现与自身注释矛盾，也说明"只读审阅态"需要一个明确的、可退出的生命周期（应用成功后应释放，而不是继续占着锁）。

**立即绕过（已告诉用户）**：在左侧「我的布局」里点一下**另一个布局** → `Selection.cs:129-131` 会调用 `ClearAiWorkspaceFrozenPreview()`（`src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceFrozenPreview.cs:621-625`）把冻结态清掉 → 线框按钮恢复可用（代价：那份冻结预览的审阅态丢失，需要时回 AI 助手重生成）。

**修复方向（待实施时定稿，未选）**：① 把「只读可视化」从冻结锁里摘出来（线框 / 拓扑预览这类永不写入的动作始终可用），只锁会改草稿或写入的动作（编辑、保存、应用）；② 若确实要锁，按钮上给出原因与"怎么解锁"（ToolTip + 一行说明），而不是静默置灰；③ 冻结态与"当前选中布局"解耦，避免切换布局等于丢弃冻结预览。

## F-5 定位记录（2026-10-15，只读代码定位，未改动）

**现象**：用户生成过工作区设计（跨屏意图设计），切到「执行」阶段后**在界面上再也看不到它**；左侧「我的布局」只有 5 项模板（AI 建议布局 / AI 建议布局 · DISPLAY1 / · DISPLAY2 / 新布局 …）。用户判断为「UI 设计 bug」——**判断成立**。

**机制（唯一出处 + 一处开关）**：

1. `src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopLayoutStagePresentation.cs:36-46` `ElementOwners`（阶段归属表，注释写明「名字必须与 `LayoutManagementView.xaml` 的 x:Name 一致，契约测试会逐个核对」）：
   - `["PrepareStagePanel"] = [Prepare]`
   - **`["WorkspaceLibrarySection"] = [Design]`**（仅设计阶段）
   - `["LayoutEditorSurface"] = [Design]`
   - `["LayoutCanvasCard"] = [Execute]`
   - `["LayoutWorkbenchSurface"] = [Design, Execute]`（左栏「我的布局」因此在两个阶段都在）
2. `src/DesktopAgent.WinUI/LayoutManagementView.Stages.cs:64`：`WorkspaceLibrarySection.Visibility = StageVisibility(stage, "WorkspaceLibrarySection")` → 在执行阶段直接 `Collapsed`。
3. `src/DesktopAgent.WinUI/LayoutManagementView.xaml:35`：`WorkspaceLibrarySection` 是 `Grid.Row="3"` 的整宽卡片（「工作区设计库」），`Loaded="WorkspaceLibrarySection_Loaded"`；库列表 `WorkspaceBlueprintList`（`:50`，`MaxHeight=160`）。
4. 同文件 `:103`：左栏「编辑 / 使用」两个按钮（`EditLayoutButton` / `SetActiveLayout_Click`）只作用于**模板列表**。

**结构性原因（两类资产被界面混为一谈）**：

- 「我的布局」＝**布局模板**（可复用、可保存、单目标屏分区，`workspace` 之外的另一套模型）。
- 「AI 工作区设计」＝**工作区设计**（跨屏意图设计 + 一次性冻结预览，落在 `workspace_blueprints` / `workspace_blueprint_revisions`）。
- 两者都叫「我的…」「设计…」，但**不是同一种东西**，所以工作区设计永远不会出现在左栏那 5 项里；而它唯一在界面上的家（工作区设计库卡片）只在设计阶段开门。

**附带事实**：工作区**详情面是只读的**——`LayoutManagementView.WorkspaceDetail.cs` 全文件只有两个 `_Click` 处理器（`WorkspaceDetailBack_Click` 与列表 ItemClick），**没有**「用这个设计生成精确预览 / 应用」入口。所以即便找到它，也只能"看"：要重新落到桌面必须回 AI 助手页用同一句意图重新生成精确预览。

**修复方向（待实施时定稿，未选）**：

1. **最小**：把 `WorkspaceLibrarySection` 加进执行阶段（`ElementOwners` 改成 `[Design, Execute]`）——一行改动，解决"找不到"。
2. 在工作区设计库里给每条设计加动作：「用这个设计生成精确预览」（直接进执行链路），或至少一个「在 AI 助手中打开」。
3. 详情面补「重新生成精确预览 / 应用这个设计」入口。
4. 左栏改成两类资产分区显示（「布局模板」/「工作区设计」），避免"我的…"这种词暗示错误的归属。
5. 加「把当前桌面存为设计」快照入口——用户真正想要的「我想用我现在的这个布局」目前没有承接（模板是单屏分区语义，快照不了整桌面）。

## 归类约定

- **体验**：面板 / 交互 / 文案 / 发现性
- **功能**：能力缺口（例：无人值守接线、模板语义、多屏策略）
- **可靠性**：崩溃 / 边界条件 / 失败可见性
- **性能**：耗时 / 资源占用

## 状态约定

- **待实施**：已记录，未排期
- **已排期**：已确定实施方案与验收标准
- **已实施**：已落地，标注提交或文档章节
- **不做**：记录理由（含被取代的替代方案）

## 已登记并已实施的既有条目（追溯用，不计入新建议）

| 编号 | 日期 | 事项 | 落地 |
| --- | --- | --- | --- |
| H-1 | 2026-10-14 | 概览与实际桌面不符（视图坐标 ≠ 屏幕坐标） | `DesktopViewCoordinateSpace`，文档 §23 |
| H-2 | 2026-10-14 | 一键回滚（含跨屏、撤销上一次撤销） | 文档 §24.1–§24.9 |
| H-3 | 2026-10-14 | 撤销会话应有来源标记（v13 `origin`） | 文档 §25–§26 |
| H-4 | 2026-10-15 | 引擎需能读带授权的库（数据目录可配） | `DESKTOPAGENT_DATA_DIR`，文档 §28 |
| H-5 | 2026-10-15 | 自动周期被拦下时应留下可审计痕迹（补点火 + 日志） | 文档 §29 |
| H-6 | 2026-10-15 | 产品面板预览 0 项 vs 引擎建议 17 项的分歧 | 结论：槽位分配对枚举顺序敏感，文档 §31 + 契约测试 |

## 相关记录

- 当前产品状态与遗留项：记忆 `tessel-product-usable-state-2026-10-15`
- 无人值守接线缺口：记忆 `unattended-execution-wiring-gap`
- 槽位分配顺序敏感性：记忆 `layout-slot-assignment-order-sensitivity`
- 工程文档：`docs/experiments/explorer-grid-measurement-fact-v1.md`
