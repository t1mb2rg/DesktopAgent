# Explorer 网格事实契约 v1（实测优先，推断只作为降级）

Status: **Step 1–5 implemented（Core 契约 + Persistence store + Diagnostics 写入 + Adapter 实测优先 + 策略门禁）**，
Step 6 的生产接线已完成（事实已喂给全部存活调用点，证据映射已落地，见第 10 节），
剩真实机 Physical Gate（由使用者执行）与策略调用点接线。

> 两个必须知道的限制：`DesktopAutomationPolicy.Resolve` 目前在生产代码里**没有任何调用点**
> （见第 9 节末尾），事实虽然已经一路带到 Adapter 的 `Provenance`，但还没有任何执行路径消费它；
> 另外真实机测量结果尚未产生，缺口 4（per-display vs shared phase）仍按"测量结果优先"挂着。

## 1. 为什么要做这件事

网格问题已经不是算法问题。spacing 由 `GetSpacing` 给出，相位靠样本即可确定，数学上没有未解难题。
真正卡住的是四件事：事实拿不到、留不住、没有来源标注、策略无法区分。

在这份文档之前，`DesktopExplorerGridPhaseResolver.TryInfer` 的 60% 多数投票结果与真实机实测结果
在代码里是同一个值，因此一条投票结果可以走到执行路径。这与已确认的验收标准不符。

## 2. 验收标准（已确认）

1. 实测优先。Physical Gate 通过的几何事实被接受。
2. 无法实测的显示器必须 fail closed，不得靠多数投票授权执行。
3. 推断只作为降级路径，且必须被明确标注为推断。
4. 事实是环境绑定的。拓扑、缩放、任务栏、auto-arrange 变化后，陈旧实测比推断更危险，
   因为它是"自信地错"，必须作废并显式降级。

## 3. 四个缺口（初始现状）

> 缺口 1、2 已在 Step 3 / Step 4 关闭；缺口 3 已在 Step 5 关闭（契约层，调用点仍未接线，
> 证据映射见第 10.2 节）；缺口 4 待真实机测量结果。

| # | 缺口 | 位置 |
|---|---|---|
| 1 | 探针报告只打印到窗口，没有持久化，也没有来源标注 | `src/DesktopAgent.Poc/MainWindow.ExplorerGridPhysicalProbe.cs:60` |
| 2 | Geometry Adapter 只接受 observation，没有实测事实入口，产出结果无 provenance | `src/DesktopAgent.Application/DesktopLayouts/Workspaces/Geometry/DesktopExplorerWorkspaceGeometryAdapter.cs:81` |
| 3 | Core/Automation 没有任何几何 provenance 字段，策略无法区分实测与推断，60% 共识等价于实测并可到达 `Auto` | `src/DesktopAgent.Core/Automation/DesktopAutomationPolicy.cs` |
| 4 | `ExplorerWorkspaceGeometry` 允许 per-display Phase，但 Adapter 要求 shared Phase | `src/DesktopAgent.Core/DesktopLayouts/Workspaces/Geometry/ExplorerWorkspaceGeometry.cs:257` |

第 4 项在 Physical Gate 结果出来前不改动：如果实测显示各显示器相位不同，需要改的是 Adapter 的
shared-phase 要求，不是 Core 模型。测量结果优先。

## 4. Step 1 落地的契约

`src/DesktopAgent.Core/DesktopLayouts/Workspaces/Geometry/ExplorerGridMeasurement.cs`

- `ExplorerGridEvidenceSource { Measured, Inferred }`
- 一条事实包含：DeviceName、Spacing、Phase、SampleCount、ConsensusCount、
  SpacingConstant、PhaseConstant、TopologyFingerprint、ObservedAtUtc。
- 构造即校验，无法伪造来源：
  - `Measured` 必须达到每轴 3 个主动样本、全部样本一致、spacing 与 phase 恒定。
    达不到 Physical Gate 的"实测"不能被称为实测。
  - `Inferred` 允许弱证据，但最低 2 个样本与 60% 共识，且来源标签不可改写。
- `MatchesTopology(fingerprint)`：不匹配即作废。
- `CanAuthorizeExecution(fingerprint)`：只有绑定当前拓扑的实测事实返回 true。
  这是验收标准在代码中的落点。

Core 依赖边界不变：该文件不引用 UI、COM、SQLite。

测试：`tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/Geometry/ExplorerGridMeasurementTests.cs`（8 项）。

## 5. Step 2 落地的存储

- Core 接口：`src/DesktopAgent.Core/DesktopLayouts/Workspaces/Geometry/ExplorerGridMeasurementPersistence.cs`
  （`IExplorerGridMeasurementStore`：Initialize / Append / GetActive / GetLatestByDevice / ListActive）。
  Store 只存事实与证据，不做授权判定；判定仍由 `CanAuthorizeExecution` 负责，避免换存储实现就改变口径。
- Schema：`SqliteDesktopAgentSchema.CurrentVersion` 9 → 10，新增 `EnsureV10Schema`，
  表 `explorer_grid_measurements` + 索引 `idx_grid_measurements_device_observed_desc`。
  DDL 自带 CHECK：来源只能是 0/1；phase 必须落在各自 spacing 的余数内；
  Measured 行必须 `sample_count >= 3` 且 `consensus_count = sample_count`。
  存储层写不进一条"自称实测但证据不足"的记录。
- Store：`src/DesktopAgent.Persistence/DesktopLayouts/Workspaces/Geometry/SqliteExplorerGridMeasurementStore.cs`。
  追加式写入，不修改历史；"当前事实" = 该 device_name 在当前 fingerprint 下最新一条。
  `GetLatestByDeviceAsync` 不带 fingerprint，用于显式降级：调用方要能看到"曾经实测过、但已失效"这一事实本身。
- 测试：`tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/Geometry/SqliteExplorerGridMeasurementStoreTests.cs`（6 项）。
  全套 2215/2215 通过（含 schema 版本断言 9 → 10 的同步更新）。

## 6. 一个必须解决的身份问题

`ComputeGeometryFingerprint`（`src/DesktopAgent.Application/DesktopLayouts/Workspaces/Geometry/DesktopExplorerWorkspaceGeometryAdapter.cs:415`）
包含 phase、GridOrigin、BlockedSlotIds。这带来一个循环：不知道 phase 就算不出完整 fingerprint，
而 fingerprint 正是用来判断"存进去的 phase 是否还有效"。

所以 store 的 `TopologyFingerprint` 必须是**不含 phase 派生值**的拓扑身份：
auto-arrange、spacing、每块显示器的 DeviceName / Bounds / WorkArea / DPI / IsPrimary。
完整 geometry fingerprint 继续作为 plan 与 Realization Certificate 的 identity，两者不能混用。

Step 4 已按此实现：`TopologyIdentity` 与 `GeometryFingerprint` 是两个独立身份，见第 8 节。

## 7. Step 3 落地的写入路径

- 拓扑身份：`src/DesktopAgent.Core/DesktopLayouts/Workspaces/Geometry/ExplorerGridTopologyIdentity.cs`。
  只含 auto-arrange、spacing、每块显示器的 DeviceName / Bounds / WorkArea / DPI / IsPrimary；
  不含 phase、GridOrigin、BlockedSlotIds（第 6 节的循环）。
- 记录器：`src/DesktopAgent.Poc/ExplorerGridPhysicalProbe/ExplorerGridProbeFactRecorder.cs`。
  只有 `Status == Passed` 才产生 Measured 事实；每轴至少 3 个主动落点样本；恒定标志必须为真。
  达不到门槛的显示器不写入，也不在这里补成推断事实——降级是策略层的决定，
  Diagnostics 只记录"测到了什么"和"为什么没测到"。被动占用样本只是佐证，不计入 Measured 的样本数。
- 报告新增 `TopologyIdentity` 与每轴主动样本数；PASS 后 UI 把事实写入 store，
  并在报告中显示写入内容与未写入原因。写入失败不改变物理扫描结论。
- 测试：`tests/DesktopAgent.Tests/ExplorerGridPhysicalProbe/ExplorerGridProbeFactRecorderTests.cs`（7 项）
  与 `tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/Geometry/ExplorerGridTopologyIdentityTests.cs`（8 项）。
  全套 2230/2230 通过。

## 8. Step 4 落地的 Adapter

`src/DesktopAgent.Application/DesktopLayouts/Workspaces/Geometry/DesktopExplorerWorkspaceGeometryAdapter.cs`

签名（旧单参重载保留；第 6 步起产线调用点已改传事实，见第 10.1 节）：

```csharp
public static DesktopExplorerWorkspaceGeometryResult Build(
    DesktopObservation observation,
    IReadOnlyList<ExplorerGridMeasurement>? facts = null);
```

结果新增三个属性（`Geometry` / `Issues` / `Succeeded` 语义不变）：

- `TopologyIdentity`：第 6 节的不含 phase 的拓扑身份，存储键。
- `Provenance`：每块显示器最终采用的相位来源
  （`DeviceName / Source / Spacing / Phase / SampleCount / ConsensusCount`），
  DeviceName 与 `Geometry.Displays` 一一对应。
- `DemotedFacts`：被显式降级、没有被采用的每条事实及其原因。降级必须可见，不静默丢弃。

判定顺序（实测优先，失败即 fail closed）：

1. 事实先分类。只有 `Source == Measured` 且 `MatchesTopology(TopologyIdentity)` 为真的事实才可采用；
   其余一律进入 `DemotedFacts`，原因分别为：拓扑身份不可计算、显示器不在当前拓扑、非实测来源、拓扑身份不一致（stale）。
   同一显示器多条可用事实时只采用 `ObservedAtUtc` 最新的一条，其余可见地降级。
2. 存在可用事实时，按 display 逐个决定相位：有事实的用事实相位；没有事实的必须自己形成可靠推断
   （每轴 ≥2 样本且 ≥60% 共识），否则该显示器报 `GridPhaseUnavailable` 并失败。
   此时不要求跨显示器 shared phase——不同显示器允许不同相位，这正是 `ExplorerWorkspaceGeometry` 已有的能力。
3. 一条可用事实都没有时，完全沿用既有行为：per-display 推断 → 共享相位 → 全局兜底推断，
   冲突报 `GridPhaseConflict`。**这块推断机制这一轮刻意不动**：它的语义变更需要单独确认，
   而"推断不得授权执行"的落点在 Step 5 的策略层，不在 Adapter。

两个身份彻底分开：`TopologyIdentity` 不含 phase（存储键），
`GeometryFingerprint`（`explorer-geometry-v1:`）改为逐显示器编码 phase / GridOrigin / slots / obstacles，
继续作为 plan 与 Realization Certificate 的 identity。旧实现把全局 phase 放在 fingerprint 头部，
per-display 相位下那样编码会丢信息。

测试：`tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/Geometry/DesktopExplorerWorkspaceGeometryAdapterTests.cs`
新增 9 项（实测优先、stale 降级并回退推断、Inferred 事实降级、全实测允许 per-display 相位、
混合来源对推断侧要求共识、不传事实仍拒绝相位冲突、两个身份不同且稳定、Provenance 与 Displays 一一对应）。
全套 2239/2239 通过。

## 9. Step 5 落地的策略门禁

`src/DesktopAgent.Core/Automation/DesktopAutomationPolicy.cs`

- 新增 `DesktopGeometryEvidence { Unknown = 0, Measured = 1, Inferred = 2 }`。
  `Measured` 只表示"与当前拓扑绑定的物理实测事实"，`Inferred` 表示图标位置多数投票，
  `Unknown` 表示调用方没有提供证据信息，按 fail closed 处理。
- `DesktopAutomationPolicyReason` 新增 `GeometryEvidenceNotMeasured = 24`。
- `DesktopAutomationPolicyContext` 末尾新增带默认值的 `DesktopGeometryEvidence GeometryEvidence = Unknown`，
  因此既有的位置参数构造仍然可用；`Resolve` 增加 `Enum.IsDefined` 校验。

门禁规则（插在"用户决议"与"显式用户请求"两个分支之后、"已确认长期策略"分支之前）：

| AuthorizationBasis | 证据 = Measured | 证据 ≠ Measured |
|---|---|---|
| `ConfirmedUserPolicy` | 原有矩阵（Assisted/Automatic → `Auto`） | `Queue` + `GeometryEvidenceNotMeasured` |
| `DeterministicLocal` | 原有矩阵（Automatic → `Auto`） | `Queue` + `GeometryEvidenceNotMeasured` |
| `AiOnly` | `AiOnlyUnconfirmed` | `AiOnlyUnconfirmed`（保留真实原因，不伪装成几何问题） |
| `None` | `NoExecutionAuthorization` | `NoExecutionAuthorization` |
| `DirectUserRequest` | `Auto` + `DirectUserRequest` | `Auto` + `DirectUserRequest`（**刻意豁免**，见下） |

**刻意保留的边界**：`DirectUserRequest` 不受实测门槛限制。理由是那是"用户此刻亲自授权"，
不是"推断在自我授权"；而且这是唯一能让用户在不支持测量的显示器上仍然手动完成布局的路径。
若要连显式请求也必须实测，`DesktopAutomationPolicyTests.Resolve_DirectUserRequest_IsAuthorizedByTheUserNotByTheGeometryEvidence`
这条断言必须一起改。

**使用者已确认（2026-10-13）**：保留 `DirectUserRequest` 的实测豁免；并且**策略调用点不在本步接线**——
等真实机 Probe 跑完、store 里确实存在当前拓扑的实测事实之后再接（否则 assisted 路径会因"尚未实测"普遍排队）。
这两条是产品口径，不是实现细节。

连带改动（否则新原因会被构造校验拒绝或显示为空）：

- `src/DesktopAgent.Core/Automation/DesktopPendingAction.cs:88-96`：`IsQueueReason` 加入 `GeometryEvidenceNotMeasured`。
- `src/DesktopAgent.WinUI/PendingActionListItem.cs:53-63`：`FormatQueueReason` 加入
  `GeometryEvidenceNotMeasured => "网格位置尚未实测，等待确认"`。

测试：`tests/DesktopAgent.Tests/Automation/DesktopAutomationPolicyTests.cs` 的私有 helper 增加
`geometryEvidence = Measured` 默认参数（既有授权矩阵测试因此保持原期望，它们描述的是"证据可用"时的行为），
并新增 5 个 `[Fact]` 覆盖 Inferred/Unknown 不得进 `Auto`、Measured 保持原矩阵、DirectUserRequest 豁免、
其他来源保留自身原因、非法枚举抛异常。

**尚未接线**：`DesktopAutomationPolicy.Resolve` 在生产代码里没有任何调用点
（全仓 grep 只命中定义与测试），因此第 5 步只在 Core 契约层生效。运行时真正决定能否无人值守的仍然是
`src/DesktopAgent.Application/DesktopAutomation/DesktopAutomationRuntimePolicy.cs:115-116` 的
`CanAttemptUnattendedExecution => EffectiveMode == DesktopAutomationMode.Automatic`，
其默认门是关闭的（`M3AutomationPoCDefaults.cs:27` → `ProvisionalClosed`）。
Adapter 的 `Provenance` → `DesktopGeometryEvidence` 的映射已在第 6 步落地（第 10.2 节），
但还没有任何执行路径去读它。

## 10. Step 6 落地的生产接线

### 10.1 事实从 store 流到 Adapter

- 读入口：`src/DesktopAgent.Application/DesktopLayouts/Workspaces/Geometry/DesktopExplorerGridFactReader.cs`。
  `TryReadActiveAsync(observation, ct)` 先用与写方**同一口径**推导拓扑身份
  （`ExplorerGridTopologyIdentity.TryComputeFor(observation)`，Adapter 侧同名入口 `ComputeTopologyIdentity`），
  身份推导不出来时直接返回 null 且**不访问 store**；否则 `InitializeAsync` 后
  `ListActiveAsync(拓扑身份, ct)`。因此进入 Adapter 的只有"绑定当前拓扑的存活事实"，
  stale 事实根本不会出现在事实集合里（Adapter 仍保留对 stale 事实的显式降级作为第二道防线）。
- 接线点（全部 5 个产线 `Build` 调用）：
  - `src/DesktopAgent.WindowsInterop/DesktopLayouts/Workspaces/Execution/DesktopWorkspaceDynamicExecutionCoordinator.cs`：
    初始观察、执行决策循环、验证完成三个阶段各一处，统一走私有 `TryReadGridFactsAsync`。
  - `src/DesktopAgent.WinUI/AiDesktopWorkspacePlanningService.cs`：AI 计划路径。
  - `src/DesktopAgent.WindowsInterop/.../DesktopWorkspaceExecutionCoordinator.cs` 的两处**故意不接**：
    该类只被测试构造（`WorkspaceApplyProductWiringContractTests` 断言产品侧不 `new` 它），是遗留组件。
- 接线方式是可选参数（`gridFactReader = null`）+ 可选事实（`facts = null`）：
  reader 缺失时行为与接线前逐字节一致。存储故障（非 `OperationCanceledException`）降级为"无实测事实"，
  Adapter 回退推断并把来源写进 `Provenance`——读事实失败不应该让产品失去几何能力。
- 组装点（composition root，具体 Sqlite 实现只出现在这里）：
  - `src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceApply.cs`：持有
    `SqliteExplorerGridMeasurementStore(DesktopAgentDataPaths.DefaultDatabasePath)` 包出的 reader，
    作为 `gridFactReader:` 传给 `DesktopWorkspaceApplyService`，再透传给动态执行协调器。
  - `src/DesktopAgent.WinUI/AiDesktopWorkspacePlanningService.cs`：自己持有 reader（与该层既有 `DesktopAgentDataPaths` 用法一致）。
  - 依赖方向不变：`DesktopAgent.Application` 只引用 Core，`DesktopAgent.WindowsInterop` 不引用 Persistence，
    事实只以数据（`IReadOnlyList<ExplorerGridMeasurement>`）形式穿过这些层。

### 10.2 证据映射：堵住数值转换的 fail-open

- `src/DesktopAgent.Core/Automation/DesktopGeometryEvidenceMapping.cs`：
  `ExplorerGridEvidenceSource` → `DesktopGeometryEvidence` 用**显式 switch**。
  两套枚举的数值顺序不同（`Measured=0/Inferred=1` 对 `Unknown=0/Measured=1/Inferred=2`），
  所以 `(DesktopGeometryEvidence)(int)source` 会把 `Inferred` 静默变成 `Measured`——一个"推断被升级成实测"的 fail-open。
  未知来源一律映射为 `Unknown`。
- `src/DesktopAgent.Application/DesktopLayouts/Workspaces/Geometry/DesktopExplorerGridGeometryEvidence.cs`：
  `FromProvenance(provenance)` 把 Adapter 的 `Provenance` 汇总为策略层需要的单一等级：
  空 → `Unknown`；全部实测 → `Measured`；任一显示器推断 → `Inferred`；出现未知来源 → `Unknown`。
  输入必须是**降级过滤后存活**的 `Provenance`：策略门禁本身不带拓扑指纹，
  只有 Adapter 的降级判定知道一条实测事实是否仍属于当前拓扑，所以不要直接拿 store 原始事实调用。

测试（第 6 步新增 19 项，全套 2268/2268 通过）：

- `tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/Geometry/DesktopExplorerGridGeometryEvidenceTests.cs`（10 项）：
  逐来源映射、**把数值转换的危险本身写成断言**（`(DesktopGeometryEvidence)(int)Inferred == Measured`）、
  未定义来源 fail closed、以及聚合的四种口径。
- `tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/Geometry/DesktopExplorerGridFactReaderTests.cs`（5 项）：
  身份不可计算时**完全不访问 store**（fake 一旦被调用即抛）、Initialize 先于 ListActive、
  传给 store 的指纹与 Adapter / Core 两个推导口径一致。
- `tests/DesktopAgent.Tests/Automation/DesktopAutomationPolicyEvidenceGateTests.cs`（4 项）：
  七参数构造路径的 `Unknown` 默认值（此前零覆盖）、非实测不得进 `Auto`、
  以及 `evidence × basis × mode` 全组合性质（`Auto` 只能来自实测或显式用户请求），带防空转断言。

### 10.3 仍然敞开的部分

- `DesktopAutomationPolicy.Resolve` 无调用点（见第 9 节末尾）：门禁与映射都已可测可用，
  但没有任何执行路径把 `GeometryEvidence` 传进去。真实机测量与策略调用点接线是两件独立的事。
- 无人值守的运行时开关仍是 `DesktopAutomationRuntimePolicy` 的 `CanAttemptUnattendedExecution`，默认关闭。

## 11. 实施顺序

1. Core 契约（已完成）。
2. Persistence（已完成）：按 `DeviceName + 拓扑身份` 绑定存储，fingerprint 变化即失效。见第 5 节；
   key 必须使用不含 phase 的拓扑身份，见第 6 节。
3. Diagnostics（已完成）：探针 PASS 后把每块显示器的事实写入 store，不再只打印。见第 7 节。
4. Application（已完成）：Adapter 接受可选的实测事实，Measured 优先，Inferred 降级并标注；
   实测存在时 shared-phase 要求按 per-display 放宽；并按第 6 节把拓扑身份与 geometry fingerprint 分开计算。见第 8 节。
5. Automation（契约层已完成）：策略识别几何来源，`Inferred` 不得支撑 `Auto`，不得由 `DeterministicLocal` 升级到 `Auto`。
   见第 9 节。
6. 生产接线（已完成，见第 10 节）：实测事实喂给全部存活 `Build` 调用点；Provenance 汇总为
   `DesktopGeometryEvidence` 的映射已落地（显式 switch，未知即 `Unknown`）。
   仍缺一步：把映射结果接到真正的策略调用方——而 `Resolve` 目前没有调用点。
7. 真实机 Physical Gate（三显示器拓扑）由使用者执行并保留完整 PASS 报告。
8. 策略调用点接线（使用者已确认为第 7 步之后的工作）：把 `Provenance` → `DesktopGeometryEvidence`
   的结果喂给真正的执行决策；注意只喂经 Adapter 降级判定后存活的来源，且 `Resolve` 需要先有调用者。

## 12. 边界与不变量


- Diagnostics 与产品的依赖边界保持单向：Diagnostics 写事实 → store → 产品读事实。
  探针不可从产品路径调用。已有依赖边界测试覆盖。
- `GeometryFingerprint` 覆盖 topology / workarea / spacing / phase / slots / obstacles，
  继续作为 plan 与 Realization Certificate 的 identity；store key 用第 6 节的拓扑身份，两者不混用。
- AI 仍然只有设计权威，没有精确单元格几何权威（ADR-0005）。
- 不确定即 fail closed；不新增坐标容差。

## 13. A 方案接线：无人值守执行必须有实测事实（2026-10-14）

用户在这一步拍板走 **A 方案**（推断默认 + 受控执行 + 探测做可选校准）：

- 装上就能用：首次运行不写桌面、不动图标，几何来自只读推断（spacing 直接问 `IFolderView2::GetSpacing`，
  phase 从既有图标共识推断），并在产品里**明确标注为预测**。
- 推断**不授权无人值守执行**；用户确认过的动作（`DirectUserRequest`）与证据等级无关，仍然放行。
- 真机 Probe 是**可选校准**：测过的屏升级为"实测"，没测过的屏保持监督模式。

### 13.1 接线点：B02 自动安全门（不是 preflight）

- `src/DesktopAgent.Application/DesktopAutomation/DesktopAutomationAutomaticSafetyGate.cs` 是自动链路上最后一处
  async 门（`ReadyForCoordinatorPreWriteGate` 之后才进 Coordinator 的 pre-write gate），而且它手里已经有本轮的目标
  显示器：`handoff.FreshCandidate.TargetDeviceName`。证据读取本身是 async（现抓一次桌面观察 + 查 SQLite），
  放在这里不需要把任何同步 delegate 改成 async。
- 新增状态 `DesktopAutomationAutomaticSafetyGateStatus.GeometryEvidenceNotMeasured = 9`：证据不是 `Measured`
  时返回它，`MayEnterCoordinatorPreWriteGate` 为 false，拒绝原因带上显示器名与补救动作
  （去 Diagnostics 的 Explorer Grid Physical Probe 页对该屏跑一次 Physical Probe）。
- 检查位置在 fresh risk 之后、返回前的 lifecycle/runtime 复核**之前**：证据读取跨越 async 边界，
  所以"读完之后再确认旧票仍有效"的既有顺序被保留。

### 13.2 证据来源：`DesktopGridGeometryEvidenceProvider`

- `src/DesktopAgent.Application/DesktopAutomation/DesktopGridGeometryEvidenceProvider.cs`
  （接口 `IDesktopGridGeometryEvidenceProvider`）：抓一次 `IDesktopObservationSource`，用与写方**同一口径**推
  `DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity`，再 `InitializeAsync` +
  `ListActiveAsync(拓扑身份)`，最后**逐屏**取该显示器的当前事实，经 `DesktopGeometryEvidenceMapping.From`
  映射成证据等级。
- fail closed 的每一条路径都返回 `Unknown`：目标显示器名为空（连桌面都不读）、观察不可用、身份推不出来、
  store 抛异常、以及 store 没按拓扑过滤时读方自己的 `MatchesTopology` 复核不通过。
- "另一块屏测过"不等于"这块屏能自动执行"：事实本来就是逐屏绑定的。

### 13.3 测试

- `tests/DesktopAgent.Tests/DesktopAutomation/DesktopGridGeometryEvidenceProviderTests.cs`（新增）：逐来源映射、
  别屏事实、陈旧拓扑、无事实、观察不可用/无显示器时**完全不访问 store**（fake 一旦被调用即抛）、store 抛异常
  fail closed、空显示器名不读桌面、与写方指纹一致、构造参数校验。
- `tests/DesktopAgent.Tests/DesktopAutomation/DesktopAutomationAutomaticSafetyGateTests.cs`：新增
  `GeometryEvidenceNotMeasured_BlocksUnattendedExecution`、
  `InferredGeometryEvidence_IsStillNotEnoughForUnattendedExecution`、
  `MeasuredGeometryEvidence_LetsTheCycleReachTheCoordinatorPreWriteGate`；fixture 的 `Gate(...)` 增加第三个可选参数
  （既有用例默认注入 `Measured`，语义不变），构造器契约测试新增"安全门必须依赖证据提供者"的正向断言。
- `DesktopAutomationExecutionCycleIntegrationTests` 的接线测试注入固定 `Measured`：集成路径关心的是
  preflight → 安全门 → 协调器的接线，证据门有自己的测试。
- `src/DesktopAgent.Poc/App.xaml.cs`（composition root）：注册
  `IDesktopGridGeometryEvidenceProvider → DesktopGridGeometryEvidenceProvider`，与
  `IExplorerGridMeasurementStore`（:230）同一个容器。

### 13.4 仍然敞开的

- 监督模式的确认方式还没定（每次弹计划确认 / 直接执行 + 回执与一键回滚 / 首次弹之后记住）——等用户拍板。
- `DesktopAutomationPolicy.Resolve` 仍然零调用点：A 的门禁语义在自动门里直接落地（"非实测不自动"），
  策略层那份矩阵继续作为契约层资产；等监督模式的确认口径定下来再决定是否合并成一处。
- `\\.\DISPLAY2` / `\\.\DISPLAY3` 的实测仍是优化项（见 `explorer-grid-physical-probe-v1.md`）。

## 14. 非矩形桌面：显示器之外的图标不再让整体规划失败（2026-10-14）

真机只读复验（`%TEMP%\gridevidence` 反射宿主，直连产品 DLL + 真库，零写入）暴露了一条与本方案无关、
但会直接掐死规划链路的既有规则：

- `DesktopExplorerWorkspaceGeometryAdapter.Build` 只要 `Issues` 非空就返回 `Failure`，而
  `IconOutsideAssignedDisplay`（图标落点不在其**归属**显示器范围内）被判为致命。这台机器的左侧
  124px 任务栏条带（x=14/90，y 一直到 1766）与 DISPLAY3 下方（y=2534）共 19 个图标因此触发它 →
  `succeeded=False`，规划在本机完全不可用。
- 同一次复验同时确认证据路径是对的：读取时重算的拓扑身份与写库时一致
  （`explorer-topology-v1:9F7C7810…`），`PROVIDER \\.\DISPLAY1 -> Measured`，DISPLAY2/3 → `Unknown`。

### 14.1 语义修正：分三类，而不是一类

| 图标位置 | 旧行为 | 新行为 |
|---|---|---|
| 不在**任何**显示器 `Bounds` 内（左条带、显示器下方） | 致命 `IconOutsideAssignedDisplay` | **通知** `IconOutsideAllDisplays = 9`：不进 slot surface、保持原位、不参与本轮规划 |
| 在**别的**显示器 `Bounds` 内 | 致命 | 仍然致命——这类图标会被写到别的屏上 |
| 归属显示器未知 | 致命 `UnknownIconDisplay` | 不变 |

理由：产品无法替用户判断"这个图标为什么在一块屏之外"，但它能证明"这个图标不在本轮规划范围内"。
降级为通知**不放宽任何写入约束**——通知项不进 slot surface、不参与规划、也不被移动。
新增 `DesktopExplorerWorkspaceGeometryResult.Notices`（与 `Issues` 正交；`Succeeded` 仍然只看
`Geometry` 与 `Issues`）。

### 14.2 测试

- `tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/Geometry/DesktopExplorerWorkspaceGeometryAdapterTests.cs`：
  `Build_IconsOutsideEveryDisplay_AreReportedAsNoticesInsteadOfFailingTheMachine`（单屏 + 一个在所有显示器
  之外的图标 → `Succeeded`、`Issues` 为空、`Notices` 带该图标名）与
  `Build_IconInsideAnotherDisplayThanItsAssignedOne_StillFailsClosed`。
- 真机复验（`%TEMP%\gridevidence` 只读宿主，修复后）：`ADAPTER succeeded=True demoted=0`，18 条
  `IconOutsideAllDisplays` 通知、0 条致命 issue；修复前同一份真实观察是 19 条致命
  `IconOutsideAssignedDisplay`、`succeeded=False`。（同一次复验：`IDENTITY adapter=core same=True`、
  `STORE active=1`、`PROVIDER \\.\DISPLAY1 -> Measured`、DISPLAY2/3 → `Unknown`。）

### 14.3 仍然敞开的

- `Notices` 目前没有产品内呈现出口（既没有 UI 消费点，也不进 `Provenance`）；本轮只保证它不阻断规划。

## 15. 探测 seed 不再写到别的屏（2026-10-14）

DISPLAY2 连续三次被 Explorer 把 seed 回写到 DISPLAY1 的 (2,702)。根因是 seed 候选槽位用"探针自己的落点"
推相位：快捷方式被 Explorer 建到别的屏后，那个落点属于**那块屏**的晶格，于是目标屏的候选槽位被算到别的
晶格上，写出去就落到别的显示器（DISPLAY3 的 seed 被推到 (2,16)、而该屏 46/48 个既有图标在 (35,84)，同源）。
两处修改：

- `ExplorerGridPhysicalProbeRunner.FindSafeSeedRequest` 的相位提示改为**该屏自己的**既有图标晶格共识
  （`ComputeIconLatticeConsensus(...).IsReliable` 时用它的 `Phase`，否则保留旧回退）。
- seed 重试循环现在只要落点跑出目标屏 `Bounds` 就停止重试（旧行为会重试 3 次，每次都真实移动用户图标）；
  "落地不在目标屏 WorkArea 内"的未实测原因里补上该屏锚点数量与晶格相位。

细节与测试见 `docs/experiments/explorer-grid-physical-probe-v1.md` 的
"Seeding on the display's own icon lattice" 一节。

## 16. 监督确认方式 (c)：逐屏站定授权（2026-10-14）

用户在三种口径里选了 **(c) 首次针对某块屏确认一次、之后记住该屏**：应用确认对话框上多一个复选框，
勾选后**只在成功应用之后**记录该屏的授权；授权绑定签发时的拓扑指纹，换屏、改分辨率、移动任务栏后自动失效。

### 16.1 授权与证据是两件事（因此两张表）

- 证据（`explorer_grid_measurements`）回答"图标会落在哪"；授权（`explorer_grid_display_authorizations`）
  回答"人是否已经同意它自己动手"。把两者混在一起，会让"实测过"看起来像"已经同意过"，
  所以分表存储、各自绑定同一个拓扑指纹。
- Core：`ExplorerGridDisplayAuthorization`（`State / DeviceName / TopologyFingerprint / AuthorizedAtUtc`，
  `AuthorizesUnattendedExecution(fingerprint)`）+ `IExplorerGridDisplayAuthorizationStore`
  （`AppendAsync` / `GetActiveAsync` / `GetLatestByDeviceAsync` / `ListActiveAsync`）。
  记录 append-only：撤销（`Revoked`）同样留痕，而不是靠记录消失表达。
- Persistence：schema v12 建 `explorer_grid_display_authorizations`（`state IN (0,1)`、UTC 时间戳 CHECK、
  `device_name` 非空白）+ 索引 `idx_grid_authorizations_device_recorded_desc`；
  老库按 `PRAGMA user_version` 升级，既有表不动。

### 16.2 自动门现在要求"实测 + 站定授权"两件

- `src/DesktopAgent.Application/DesktopAutomation/DesktopAutomationAutomaticSafetyGate.cs` 新增
  `DisplayNotAuthorizedForUnattendedExecution = 10`。证据门（9）与授权门（10）分开报，
  因为补救动作不同：9 = 去 Probe 页校准，10 = 当面确认一次并勾选记住这块屏。
- 取数只在一次桌面观察里完成：`DesktopGridDisplayClearanceProvider.GetClearanceAsync(deviceName)`
  现抓一次观察 → 用同一指纹同时查两张表 → `DesktopGridDisplayClearance(GeometryEvidence, IsAuthorizedForUnattendedExecution)`；
  证据不是 `Measured` 时授权位**主动降为 false**（授权不能脱离证据存在）。
- 六条 fail-closed 路径（目标屏名为空、观察不可用、身份推不出、store 抛异常、非实测、无授权/已撤销/指纹不符）一律不可授权。
- 旧的 `DesktopGridGeometryEvidenceProvider`（§13.2）被它取代并删除。

### 16.3 写入路径只在"已经成功应用"之后

- `src/DesktopAgent.WinUI/LayoutManagementView.WorkspaceApply.cs` 的确认对话框加了复选框
  （"在这套显示设置下记住参与设计的 N 块显示器，之后允许无人值守执行"）；仅在
  `Status == Committed` 且勾选时调用 `DesktopGridDisplayAuthorizationGrantService.GrantAsync(...)`
  ——先看到它成功，才记住它。
- 参与设计的屏按 `DesktopWorkspaceFrozenPreviewDisplay.Policy == Design` 筛出；
  因为授权表按真实设备名记录，`DesktopWorkspaceFrozenPreviewDisplay` 新增 `DeviceName`
  （`DisplayRef` 是逻辑引用 primary/secondary_1，不是设备名）。
- 签发服务自己重新抓一次观察：它必须绑定"写入当时"的显示设置，而不是几秒前那份预览里的快照。
  五种结局（`Granted` / `NothingRequested` / `ObservationUnavailable` / `TopologyUnavailable` / `StoreFailed`）
  各自有一条面向用户的中文说明；`StoreFailed` 如实报告已写入的子集，但整体结论是失败，且不回滚已经成功的应用。

### 16.4 测试

- `tests/DesktopAgent.Tests/DesktopAutomation/DesktopGridDisplayAuthorizationGrantServiceTests.cs`（12 项）：
  逐屏签发并绑定指纹、去重与 trim、没有任何可用屏名时**连桌面都不读**、观察不可用/抛异常、拓扑推不出、
  store 初始化失败、第 N 次写入失败（报告部分写入但整体失败）、重复授权追加而非覆盖、
  真 SQLite 往返（同指纹可读回、陈旧指纹与别的屏不可授权）、已取消 token、构造参数校验。
- `tests/DesktopAgent.Tests/DesktopAutomation/DesktopGridDisplayClearanceProviderTests.cs`（22 项，取代旧 provider 测试）：
  来源映射、逐屏授权、`MeasuredEvidenceWithoutDisplayAuthorization_BlocksUnattendedExecution`、
  观察不可用/无显示器时完全不访问 store、两个 store 必须收到同一指纹、clearance 降级等。
- `tests/DesktopAgent.Tests/Persistence/SqliteDesktopAgentSchemaTests.cs`：新表与索引进入完整性清单、
  `EveryStore_UsesSharedV12Initializer_AndRepeatedInitIsIdempotent`（9 个 store）、
  `V12Upgrade_AddsDisplayAuthorizationTableWithoutTouchingExistingTables`（从 v11 库升级）。

### 16.5 仍然敞开的

- 撤销入口已在 §17 落地（"已记住的显示器…"按钮 + `Revoked` 追加）；「撤销一次成功的应用」仍无路可走（见 §17.5）。
- 授权目前只为自动链路服务：选项 (b)「直接执行 + 回执与一键回滚」与选项 (a)「每次弹计划确认」都还没实现，
  因此现在勾选记住与否，只影响 unattended 路径。
- 本机三屏里只有 DISPLAY1 有实测事实（见 §13.4），所以无人值守目前最多只在 DISPLAY1 上成立。

## 17. 收回授权：看得见、收得回（2026-10-14）

授权只有"能授予"还不够：一项长期决定必须能被看见，也必须能被收回，否则它就不是决定，而是一次事故。

### 17.1 看得见：只列当前环境下的授权

- `src/DesktopAgent.Application/DesktopAutomation/DesktopGridDisplayAuthorizationQueryService.cs`：
  一次桌面观察 → `ComputeTopologyIdentity` → `ListActiveAsync(fingerprint)`，再**只保留 `State == Granted`**。
  store 的 `ListActiveAsync` 语义是"每块屏最新一条决定"（可能已经是 `Revoked`），
  最新一条决定是收回时这块屏就不在"当前记住的显示器"里 —— 过滤发生在调用方，
  而不是靠 store 少返回（否则"收回"看起来会像"记录消失"）。
- 读不出来必须说"读不出来"：`DesktopGridDisplayAuthorizationQueryStatus` 区分
  `Available` / `ObservationUnavailable` / `TopologyUnavailable` / `StoreFailed`；
  `StoreFailed` 绝不退化成"当前没有授权"——"不知道"和"没有"在界面上是两句话。
- 返回的是带指纹的快照（`DesktopGridDisplayAuthorizationSnapshot`），调用方不需要再去猜它读的是哪套显示设置。

### 17.2 收得回：撤销也是追加，而且绑在当前显示设置上

- `src/DesktopAgent.Application/DesktopAutomation/DesktopGridDisplayAuthorizationRevokeService.cs`：
  与签发同一套前置（trim/去重 → 没有任何可用屏名则 `NothingRequested` 且**连桌面都不读** → 观察 → 指纹推不出则 `TopologyUnavailable`），
  然后逐屏 `AppendAsync(Revoked, 设备名, 当前指纹, now)`。
  - 撤销**不移动任何桌面项目**，也不删除历史：`Revoked` 是一条新记录，`GetLatestByDeviceAsync` 仍能读出"它被收回过"。
  - 撤销绑在**当前**拓扑指纹上：换屏之后收回旧环境里的授权，不会误伤新环境里可能存在的授予。
  - 撤销不要求这块屏之前真的被授权过：收回是一个决定，不是一次结算。
  - 部分写入（第 N 条失败）整体报 `StoreFailed` 并如实列出已落盘子集——半途而废不是收回。

### 17.3 产品入口与回执

- `src/DesktopAgent.WinUI/LayoutManagementView.xaml` 在"工作区设计库"标题行右侧加了「已记住的显示器…」按钮
  （与刷新按钮同一个 `StackPanel`）；`src/DesktopAgent.WinUI/LayoutManagementView.GridDisplayAuthorizations.cs`
  是它的实现：读出当前授权 → 一个复选框列表（`设备名 · 记住于 本地时间`）→ 取消勾选并保存 = 收回那块屏。
  执行中（`_directApplyInProgress`）不弹列表面板；读不出来时给出一句能照实说明原因的提示，而不是一个空列表。
- 回执：应用成功后的提示补了一句"想提前收回时，点「工作区设计库」右上角的「已记住的显示器…」取消记住。"
  回执与收回在同一处可见，用户不用去猜授权记录在哪里。

### 17.4 测试

- `tests/DesktopAgent.Tests/DesktopAutomation/DesktopGridDisplayAuthorizationGovernanceTests.cs`（16 项）：
  查询侧（空库可用但无授权、只列最新一条 `Granted`、观察不可用/抛异常时完全不碰 store、指纹推不出、
  store 读失败必须报 `StoreFailed` 而不是"没有授权"、真 SQLite 库读出签发服务写下的记录）＋
  收回侧（逐屏追加 `Revoked` 且绑定当前指纹、真库上"签发→收回"后不再授权且历史仍在、
  从未授权过的屏也能收回、无可用屏名不读桌面、部分写入报失败、已取消 token、构造参数校验）。
  fake store 的 `ListActiveAsync` 按设备分组取最新一条，与真实 SQLite store 同构——
  否则调用方的去重逻辑会被测试掩盖。

### 17.5 「撤销一次成功的应用」还没有路可走

问：点一次能不能把**已经成功应用**的布局退回原样？查证结果：现在不能，缺的不是数据而是执行路径。

- 收据与快照都还在：`RecoverySessionRecord`（`src/DesktopAgent.Core/DesktopLayouts/Restore/Execution/RecoverySessionRecord.cs`）
  带 `SafetySnapshotId`、`SourceRestorePlan`、`ExecutionPlan`、`RollbackAttempts`、`CompletedForwardStepIndexes`；
  `IDesktopLayoutSnapshotStore.GetAsync(snapshotId)`（`src/DesktopAgent.Persistence/DesktopLayouts/SqliteDesktopLayoutSnapshotStore.cs`）
  能取回应用前的快照。
- 但只有失败路径会回滚：`LayoutManagementView.WorkspaceApply.cs` 的 `Restored` 分支与
  `LayoutManagementView.DirectApply.cs` 的 `FailedAndRolledBack` 分支；成功提交之后没有任何入口重新触发回滚管线。
- `IRecoverySessionStore` 只暴露 `GetAsync(Guid)` 与 `ListUnresolvedAsync(limit)`：成功提交的会话不是"未解决"，
  按现有查询根本取不回来。
- 结论：一键回滚 = 新增一条执行路径（按已提交会话的安全快照构造还原计划 → 走既有的校验/回滚管线 → 重新观察确认），
  外加一个能列出"最近一次成功应用"的入口。这是一块独立的工作，建议排在真机端到端监督演练之后——
  演练会先告诉我们"成功之后"的现场究竟长什么样。

### 17.6 仍然敞开的

- `DesktopAutomationPolicy.Resolve` 仍未接生产调用点（自动链路的证据门与授权门已由 safety gate 直接实现）。
- 一键回滚未做（见 §17.5）。
- (a)「每次弹计划确认」未实现。
- 本机只有 DISPLAY1 有实测事实。

## 18. 产品库与工程库是两套：无人值守在今天的真机产品里无法成立（2026-10-14）

真机验证（启动 `src\DesktopAgent.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\DesktopAgent.WinUI.exe`，
走"布局 → 已记住的显示器…"）得到两件事实：

1. 授权界面在产品里是通的：对话框如实报"当前没有记住任何显示器"（并说明"在这套显示设置下 Tessel 还不能无人值守执行"），
   产品库随即升到 `user_version = 12` 并建出 `explorer_grid_display_authorizations`。
2. 但产品库与工程库不是同一个文件：
   - 产品（Tessel Dev）：`%LOCALAPPDATA%\Tessel.Dev\desktop-agent.db`
     （`src/DesktopAgent.WinUI/TesselProductDataPaths.cs` → `TesselProductIdentity.LocalDataDirectoryName`）
     ——实测事实 0 行、授权 0 行。
   - 工程（Diagnostics）：`%LOCALAPPDATA%\DesktopAgent\desktop-agent.db`（Persistence 的默认路径）
     ——2 行 `\\.\DISPLAY1` 实测事实（`evidence_source = 0`、`spacing = (76,98)`、`phase = (2,16)`、`samples = 33`）。
   这是刻意设计的：`src/DesktopAgent.WinUI/ProductDataPathFacade.cs` 在产品程序集内遮蔽 Persistence 的默认路径，
   让 Dev / Packaging Test / Stable 各用自己的数据根，"Diagnostics 继续保持原有工程数据路径语义"。

后果：能产生实测事实的只有 Diagnostics 里的 Physical Probe（`src/DesktopAgent.Poc/ExplorerGridPhysicalProbe/`，
WinUI 工程并不引用 Poc），而它写的是工程库；产品库永远拿不到实测事实 →
`DesktopGridDisplayClearanceProvider` 在产品里恒为"证据 Unknown" → safety gate 的 9 号拒绝恒成立 →
**无人值守在产品里今天不可能发生**。（另有 `DesktopAutomationUnattendedGate` 默认关闭、当前模式是 Assisted
两道政策闸门，但即使它们打开也不会有证据。）

这不阻塞监督路径：用户当面确认走 `DirectUserRequest` 豁免，与证据等级无关。所以真机监督演练不受影响。

三条待定的路：

- (A) 把探测搬进产品（产品内"校准这块屏"入口，事实写进产品库）：终端用户可用，是发布前必须做的事；
  需要把 probe runner 从 Poc 移到可共享工程，并配套产品页面与进度/结果文案。
- (B) 先做开发期通路：让 Diagnostics 探测可以指定产品数据根（`--data-dir` / 环境变量），演练与联调立刻可做，
  但这不是能发布给终端用户的形态。
- (C) 维持现状，并把"无人值守尚未在产品中可用"写进发布说明。

## 19. 校准搬进产品面板：探测进入共享层，实测事实回到产品库（2026-10-14）

§18 的三条路里选 (A)：把探测做成客户看得见的面板里的一次显式动作，而不是继续留在工程工作台。

### 19.1 探测运行器搬到共享层

- `src/DesktopAgent.Poc/ExplorerGridPhysicalProbe/` 的 5 个文件整体搬到
  `src/DesktopAgent.WindowsInterop/ExplorerGridPhysicalProbe/`（runner / contracts / plan / recorder / `.url` 快捷方式存储），
  命名空间 `DesktopAgent.Poc.ExplorerGridPhysicalProbe` → `DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe`。
- 为什么是 WindowsInterop：它已引用 Core + Application，而两个 store 接口都在 Core
  （`src/DesktopAgent.Core/DesktopLayouts/Workspaces/Geometry/ExplorerGridMeasurementPersistence.cs`、
  `ExplorerGridDisplayAuthorizationPersistence.cs`）；产品壳本来就引用 WindowsInterop。
  于是 `DiagnosticsWorkbenchSplitContractTests.ProductWinUi_DoesNotReferenceDiagnosticsWorkbenchProject` 继续成立——
  产品依然没有反向引用诊断工作台工程。
- 运行器头部注释随之改写：不再是 "Diagnostics-only"，而是"诊断工作台与产品校准面板共用的真机探测"，
  仍然不参与 Workspace / AI / Recovery / 自动执行 authority，唯一产物是 Measured 事实。

### 19.2 共用会话层：报告排版与落盘只有一份

`src/DesktopAgent.WindowsInterop/ExplorerGridPhysicalProbe/ExplorerGridProbeSession.cs`：
跑扫描 → 写事实 → `report.Format() + FormatFactSummary(...)` → 落盘。两条不许分叉的规矩住在这里：

- 事实写入失败不改变物理扫描结论：`RecordFactsAsync` 把异常转成可读的 Rejections，报告如实显示"未写入：…"；
- 报告落盘失败绝不影响结论：`PersistReport` 返回 `null`，界面里仍有完整报告。

`LogDirectoryFor(databasePath)` = `<数据库所在目录>\logs`；`ExplorerGridProbeSessionResult` 带 `Status` 与 `MeasuredDisplayCount`。
WPF 页面（`src/DesktopAgent.Poc/MainWindow.ExplorerGridPhysicalProbe.cs`）删掉了自己那三个私有方法，改用这一份。

### 19.3 产品面板里的入口

- `src/DesktopAgent.WinUI/LayoutManagementView.xaml` 在「已记住的显示器…」左边加了「校准桌面网格…」（`GridProbeButton`）。
- `src/DesktopAgent.WinUI/LayoutManagementView.GridProbe.cs`：
  确认框里必须勾上"我明白：校准期间不要拖动图标、增删桌面文件、切换自动排列/网格，也不要改变显示器拓扑"
  才允许点「开始校准」；运行期间按钮变成「校准中…（点此停止）」，进度写进工作区状态行；
  结束后弹结果对话框（结论 + `实测 N/M 块显示器` + 完整报告 + 「复制报告」）。
- 物理写入器不进 UI 层：`ExplorerGridPhysicalProbeRunnerFactory.Create(observationSource, staThreadExecutor)`
  （`src/DesktopAgent.WindowsInterop/ExplorerGridPhysicalProbe/ExplorerGridPhysicalProbeRunnerFactory.cs`）
  组装 `NativeDisplayTopologyReader` + `StableDesktopObservationSource(500ms)` + `DesktopShellIconPositionWriter`
  + `DesktopInternetShortcutStore`。这条边界原本由
  `M5077DirectLayoutApplyUiContractTests.AllProductLayoutPartials_KeepPhysicalWriterAndInternalScopeGatesOutOfWinUi`
  守着（它枚举 `LayoutManagementView*.cs` 并禁止出现 `DesktopShellIconPositionWriter`/`IDesktopIconPositionWriter`），
  新契约测试再单独断言一次。
- 数据根：产品壳里 `src/DesktopAgent.WinUI/ProductDataPathFacade.cs` 遮蔽 Persistence 默认路径，
  所以面板写的是**产品库**（`%LOCALAPPDATA%\Tessel.Dev\desktop-agent.db`）——§18 那条
  "产品库永远拿不到实测事实"由此闭合。结果是逐屏的：拿到合格样本的屏写 Measured，测不到的屏如实记未实测，
  界面明说"这些屏只能监督执行"、"不会用推断顶替"。

### 19.4 测试

- `tests/DesktopAgent.Tests/ExplorerGridPhysicalProbe/ExplorerGridProbeSessionTests.cs`（8 项）：`LogDirectoryFor`、
  `FormatFactSummary`（有事实/无事实两态）、`PersistReport`（latest + 带时间戳副本；路径不可用时返回 `null` 而不是抛）、
  `RecordFactsAsync`（store 抛异常 → 保留扫描结论并写明原因；正常 → 写入 Measured 事实）。
- `tests/DesktopAgent.Tests/Productization/ProductGridCalibrationSurfaceContractTests.cs`（7 项）：入口与授权入口并存、
  同意才可开跑、复用共用会话（面板里没有自己的报告排版，也没有 `File.WriteAllText`）、物理写入器留在共享层、
  未实测屏文案、运行器住在 WindowsInterop 且 `src/DesktopAgent.Poc/ExplorerGridPhysicalProbe` 已不存在、
  WPF 页仍复用同一会话。
- 全量回归：`dotnet test tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Debug` → 2367 项全绿。

### 19.5 仍然敞开的

- 面板还没在真机上点过一次（点下去会真的移动图标约 1 分钟）——这是下一步要用户亲自确认的动作。
- 一键回滚（§17.5）、`DesktopAutomationPolicy.Resolve` 的生产调用点、(a) 每次弹计划确认、DISPLAY2/3 的实测。

## 20. 执行面收口：自动执行只落在一块目标屏（2026-10-14）

### 20.1 为什么收口

"我们的规划现在是所有屏幕的，但校准只能一块一块做" —— 这句话点出的是**执行目标的唯一性**，不是校准的能力范围。
探测对每块屏都跑，门槛本来就逐屏；但如果一次自动执行同时把图标放到两块屏上，就没有任何一块屏的
实测网格与站定授权能覆盖整个动作：DISPLAY1 测准了，不代表 DISPLAY2 上的目标格也是测出来的。
所以收口的口径是：**来源不限，目标唯一**。

### 20.2 规则落在验证器，不靠调用方自觉

`DesktopDesiredWorkspacePlanValidator` 新增 `MultipleTargetDisplays = 25`：所有 `Place` 项的目标屏必须唯一。
`Keep`/`Exclude` 不设目标屏，所以"其余屏保持现状"完全合法；来源跨屏也照旧合法——收口只约束目标。
验证器本来就在生产路径上被调用（`src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopLogicalWorkspacePlanningSessionCoordinator.cs:46`、
`src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopLogicalWorkspacePlanningSessionCoordinator.cs:216`、
`src/DesktopAgent.Application/Intelligence/OpenAiCompatibleDesktopLogicalWorkspacePlanner.cs:285`、
`src/DesktopAgent.Core/DesktopLayouts/Workspaces/DesktopLogicalWorkspaceFrozenPreview.cs:154`），
所以这条规则不需要任何新接线就已经生效；新增的 issue code 只是 Plan 校验结果，不进持久化 schema（无迁移）。

### 20.3 面板上的「目标屏」磁贴

`src/DesktopAgent.WinUI/LayoutManagementView.LogicalWorkspacePreview.cs` 原先写的是
`preview.Displays.Count == 1 ? display.DisplayRef : "多屏逻辑工作区"` —— 它把"逻辑工作区跨了几块屏"显示成目标屏，
正是这次要消掉的混淆。现在按 `Place` 项去重后的唯一目标屏显示：
0 块 → `保持现状（不移动任何图标）`；1 块 → 该屏 ref；多块 → `N 块目标屏（不合法：自动执行只允许一块）`。

### 20.4 测试

- `tests/DesktopAgent.Tests/DesktopLayouts/Workspaces/DesktopDesiredWorkspacePlanValidatorTests.cs` 新增两项：
  两块屏各放一个（两边格位各自合法）→ 唯一失败原因就是目标不唯一，消息点名两块屏并含"目标必须唯一"；
  来源跨屏 + 其他屏 Keep → 不触发该规则。
- `tests/DesktopAgent.Tests/Productization/ProductSingleTargetDisplayContractTests.cs`（3 项）：验证器里的规则码与文案、
  预览里不再出现"多屏逻辑工作区"、XAML 仍保留单一「目标屏」磁贴。

### 20.5 那次真机校准确实发生过（补 §19.5 第一条）

2026-10-14 13:28:50Z–13:30:32Z（约 102 秒），用户在**产品面板**上点了「校准桌面网格…」：
`Status=Partial`、`MeasuredDisplays=1/3`、`UserIconsUnchanged=True`、`UserIconsRepaired=46 unrepaired=0`、
`OwnedShortcutsDeleted=True`。`\\.\DISPLAY1` 拿到合格样本（`spacing=(76,98)`、`phase=(2,16)`、图标晶格共识 `(2,16) support=41/48`）；
`\\.\DISPLAY2` 如实记未实测（seed 落点被 Explorer 放到 `(2,702)`，落在该屏 WorkArea 之外，屏内既有锚点 0 个）；
`\\.\DISPLAY3` 如实记未实测（seed 推出的 `phase=(2,16)` 与屏内 46/48 个既有图标的晶格 `phase=(35,84)` 不一致）。
报告落在**产品**日志目录 `%LOCALAPPDATA%\Tessel.Dev\logs\grid-probe-latest.txt`（14232 字节），产品库由此第一次写入自己的 Measured 事实。

### 20.6 仍然敞开的

- 计划器/蓝图绑定还不会自动按"一块目标屏"生成 Plan：现在多目标会被验证器拦下（fail closed，图标不动），
  下一步是让绑定侧把非目标屏统一落成 Keep，让收口从"拦"变成"引导"。
- 一键回滚（§17.5）。
- DISPLAY2/3 的实测条件：每块屏都要有自己的合格样本（`\\.\DISPLAY3` 那条说明桌面图标不在探针要测的网格上，属于真实环境约束）。
- 生产里仍没有 `DesktopAutomationPolicy.Resolve` 的调用点：收口保证"目标唯一"，
  但"这块屏实测 + 已站定授权才允许无人值守"这一步要到接线时才真正生效（证据提供者与逐屏授权查询已就位）。

### 20.7 对外措辞：别把产品说小

"当前只支持对单一屏幕网格固定"是**不准确**的说法，它把两件事混成了一件：网格固定是**逐屏**的
（每块屏有自己的 `spacing`/`phase` 事实：这次 `\\.\DISPLAY1` 拿到 `(76,98)/(2,16)`，另外两块屏各自如实记未实测），
多屏机器上每块屏都能各自被校准、各自被授权。真正的限制只有一条：**一次自动执行的目标屏唯一**。

对外统一句式：**"多屏可用；校准逐屏进行；一次自动执行只落在一块目标屏，其他屏保持现状。"**
落地位置：面板「目标屏」磁贴的可见小字「一次执行只落这一块屏」，以及该磁贴 ToolTip 的完整句式
（`src/DesktopAgent.WinUI/LayoutManagementView.xaml:84`）；由
`tests/DesktopAgent.Tests/Productization/ProductSingleTargetDisplayContractTests.cs` 的
`ProductLayoutSurface_StatesTheSingleTargetDisplayBoundaryBeforeTheAction` 钉住，发布说明按同一句式写。

### 21 面板 L1：就绪条（一处判定、一处渲染）

面板此前把「应用到桌面」的启用、文案与可见性写在 **6 个 partial 的 18 处** 里，
于是"点不动但不知道缺什么"，而这句话背后的判定没有任何单一出处。L1 把它收成一处，并且是可穷举的纯函数：

- 判定：`src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopApplyReadiness.cs`
  - 输入 `DesktopApplyReadinessInputs`（预览来自哪条路径、是否执行中、fresh 预览是否完整/要移动几项、
    Frozen Workspace 的一次性票是否还在/要移动几项、以及绑当次证据的 override）→ `Evaluate()`；
  - 结论 `DesktopApplyReadiness(CanApply, ButtonText, Reason, Headline, Detail)`：
    「为什么不能」和「下一步怎么办」是同一次产出，界面不再自己拼文案；
  - 优先级：`ExecuteInProgress` → 证据 override → 路径判定。文案：Frozen Workspace = 「应用这个布局」，
    其余 = 「应用到桌面」，阻断期间不来回跳。
- 界面：`src/DesktopAgent.WinUI/LayoutManagementView.Readiness.cs` 是**唯一**写
  `ApplyToDesktopButton.Content/IsEnabled` 和就绪条文字的地方；就绪条是
  `LayoutManagementView.xaml` 的 `ApplyReadinessBar`（Row 3，在动作行 Row 4 之前：先说清缺什么，再给按钮）。
- 旧站点处置：`DirectApply.cs` 6 处、`PreviewGate.cs` 3 处、`Recovery.cs` 2 处、
  `LogicalWorkspacePreview.cs`/`WorkspaceFrozenPreview.cs` 各 1 处 → 改为按当前证据重算；
  `WorkspaceApply.cs` 的原判定函数删除。两处刻意收紧：计划不可执行、预览失败期间入口保持关闭
  （理由绑当次证据，成功的新预览会自动失效），而不是"启用但点了才报错"。
- 测试：`tests/DesktopAgent.Tests/Productization/DesktopApplyReadinessTests.cs`（12 项，含全组合穷举：
  只有"完整且有变化的 fresh 预览"或"持票的 Frozen Workspace"才开放，且每次阻断都必须给出理由与下一步）；
  `tests/DesktopAgent.Tests/Productization/ProductApplyEntryContractTests.cs`（4 项：只有 Readiness 一处写按钮、
  各闸口都走重算、就绪条在动作行之前、理由文案不许在界面里出现第二份）。
- 同时更新了两条既有契约测试（它们原本钉的是"就地判定"这个**形状**，规则本身没变）：
  `M5077DirectLayoutApplyUiContractTests.ProductLayoutSurface_ExposesExplicitApplyButtonAndConfirmationDialog`
  与 `WorkspaceFrozenPreviewUiContractTests`（两处）→ 现在钉"规则住在判定里 + 界面只重算"。

验证：`dotnet test` **2390/2390** 通过；产品壳编译 0 错误（含 XAML codegen）。

**下一步**：L1b 把逐屏实测事实与站定授权查出来接进就绪条（同一次重算里报"这块屏实测过 / 已授权 / 未授权"），
让"为什么不能无人值守"也变成一句话；L2（面板三阶段）已落地，见 §22。

### 22 面板 L2：准备 → 设计 → 执行 三阶段

L1 让"点不动"有了理由，但入口本身仍然是平的：19 个按钮、10 个下拉铺在一页上，
校准/授权/恢复/桌面线框与"预览 → 应用"挤在同一条动作行里。L2 把"一次只做一件事"变成结构。

- 呈现表（唯一出处）：`src/DesktopAgent.Application/DesktopLayouts/Workspaces/DesktopLayoutStagePresentation.cs`
  - 阶段 `DesktopLayoutStage`：`Prepare`（这台机器可不可信）/ `Design`（图标摆成什么样）/ `Execute`（落到桌面）；
  - `Title` / `Hint` / `StageButtonContent` 与"哪些面归哪个阶段"（`IsVisibleInStage`）都在这张表里；
  - 未登记的元素名**当场抛异常**，不静默隐藏 —— "看不见的入口"正是这一轮要修的毛病。
- 界面：`src/DesktopAgent.WinUI/LayoutManagementView.Stages.cs`
  - 阶段条 `LayoutStageBar`（Row 2）：`准备 / 设计 / 执行` 三个按钮 + 当前阶段的一句话提示；
  - `PrepareStagePanel`（Row 4）：校准桌面网格、已记住的显示器、处理恢复状态、桌面线框；
  - 工作台三栏按阶段收放：左（我的布局）两个阶段都在，中（画布，含预览与应用）只归执行，
    右（编辑器）只归设计；工作区详情面仍由详情流程自己开关，阶段只保证"离开设计阶段一定收起"；
  - 执行动作行只剩三步：`预览 → 应用 → 保存`（`LayoutActionRow`，契约测试数按钮个数）；
  - 进入面板的落点：一块"站定授权"都没有的机器落在**准备**阶段，其余落在**设计**阶段；
    读不到授权不拦人，留在设计阶段。
- 不改判定：阶段代码不碰 `ApplyToDesktopButton`，也不含 `DesktopApplyBlockReason` / `.Evaluate(` ——
  "能不能应用"的唯一出处仍是 `DesktopApplyReadiness`（§21）。
- 测试：`tests/DesktopAgent.Tests/Productization/ProductLayoutStagesContractTests.cs`（7 项：
  阶段表恰好三项、每个受限元素名同时存在于界面与阶段代码、未登记元素名必须报错、每个阶段都有标题与提示
  且只有当前阶段带标记、阶段代码不碰门槛、执行行恰好三步且机器类按钮不在其中、
  准备阶段说清"无人值守的两件前提"与"换屏后需要重新校准"）；
  并更新 `ProductApplyEntryContractTests.ReadinessBar_IsDeclaredBeforeTheActionRow` 的动作行锚点
  （动作行现在带 `x:Name="LayoutActionRow"`）。

验证：`dotnet test` **2397/2397** 通过；产品壳编译 0 错误（含 XAML codegen）。

**仍然敞开的**（截至本节）：L1b（逐屏实测事实与站定授权接进就绪条）、计划器按单目标屏生成 Plan
—— 按用户 2026-10-14 的口径留到发布后看反馈；多屏实测已在 §23.4 完成；
一键回滚在用户授权后已落地，见 §24。

## 23. 概览与真实桌面不符：IFolderView 用的是视图坐标，产品用的是屏幕坐标（2026-10-14）

用户报告（三块屏上的图标都贴各屏最左侧竖排，产品「概览」显示的当前桌面却对不上）。根因不是雷达的
拟合或渲染，而是**读取桌面图标时的坐标空间**：

- 桌面图标列表视图横跨**整个虚拟桌面**（真机 `GetWindowRect`）：`Progman` (0x20132) `(-1080,-210,2560,2520)`，
  其 `SHELLDLL_DefView` (0x1068A) 与 `SysListView32` (0x10C60) 的客户区原点就是虚拟桌面左上角 `(-1080,-210)`。
- `IFolderView.GetItemPosition` / `SelectAndPositionItems` 用的都是这个**视图客户区坐标**；而产品的显示器
  `Bounds`/`WorkArea`、区域矩形、计划、雷达快照、归属判定一律用**屏幕坐标**。
- 读取侧把视图坐标直接当成屏幕坐标使用，于是每个图标的坐标都少了视图原点这份偏移。主屏原点恰好是 `(0,0)`
  时两者相等，所以这个 bug 只在"主屏不在虚拟桌面左上角"的多屏机器上显形。

### 23.1 证据（同一次真机观察，修复前后）

| 屏 | 几何 | 修复前 图标数 / 落在自己屏内 | 修复后 |
|---|---|---|---|
| `\\.\DISPLAY1` | `(0,0,2560,1440)` 主屏 | 79 / 部分 | 43 / **43** |
| `\\.\DISPLAY2` | `(-1080,-210,0,1710)` 竖屏 | 7 / 部分 | 45 / **45** |
| `\\.\DISPLAY3` | `(309,1440,2229,2520)` | 58 / 部分 | 56 / **56** |
| 合计 | | 144，其中只有 54 个落在自己屏内 | 144，**全部**落在自己屏内 |

修复前"视图坐标 `x=14/90/166`"那一组（真实位置是竖屏的 `-1066/-990/-914`）被算进主屏，主屏自己那组
（视图 `x=1218/1294/1370`）也留在主屏——一块屏的画面上画了两块屏的内容，这就是概览对不上的原因。
修复后每块屏的图标都落在自己的 `Bounds` 内，且竖屏上那排"贴底"的图标（视图 `y=1766`）回到了竖屏底部
（真实 `y=1556`），不再越界跑到 DISPLAY3 去。

### 23.2 修复：一个换算点，两侧都走它

- 新增 `src/DesktopAgent.WindowsInterop/DesktopIcons/DesktopViewCoordinateSpace.cs`：
  `ClientOrigin` + `ForWindow(hwnd)`（`ClientToScreen(0,0)`）/ `FromVirtualScreen()`
  （`GetSystemMetrics(SM_XVIRTUALSCREEN/SM_YVIRTUALSCREEN)`）/ `ToScreen` / `ToView`。
- 读取：`DesktopShellIconReader.ReadSingleIcon(..., coordinateSpace)` 读出来先 `ToScreen`；
  `DesktopViewContext.CoordinateSpace` 由两条视图获取路径（`FindWindowSW` 与 `IShellWindows` 枚举）上报，
  没记到原点时退化为虚拟桌面原点。
- 写入：`DesktopShellIconPositionWriter.MoveDesktopIcon` 的对外契约仍是**屏幕坐标**，写进去前
  `ToView`，回读的"移动前位置"与"实际落点"再 `ToScreen`（否则回执里的落点也是错的）。
- 测试：`tests/DesktopAgent.Tests/DesktopIcons/DesktopViewCoordinateSpaceTests.cs`（4 项：恒等、真机原点往返、
  无损 round-trip、读取/写入两侧都必须经过换算且原写法不许回来）。全量 `dotnet test` **2403/2403** 通过。

### 23.3 顺带解释：早先 DISPLAY2 / DISPLAY3 测不出来

§15 里"seed 被 Explorer 放到别的屏"与 §20.5 里两块屏未实测，根因同一份：探测按屏幕坐标算出的 seed
被当作视图坐标写进去，落点整体偏了 `(-1080,-210)`，所以 Explorer 把它放到了别的屏、或落在该屏 WorkArea 之外。
坐标修好之后多屏实测才具备成立条件；`\\.\DISPLAY1` 那条既有事实的 `spacing` 平移不变仍然有效，
但它的 `phase` 是在视图坐标系里算出来的，**需要重测**才会得到屏幕坐标系的相位。

### 23.4 修复后的真机重测：三块屏全部测出（2026-10-14 22:51–22:53）

用户在修复后的产品面板上点了「校准桌面网格…」（约 112 秒），结果从"1/3 块屏 + 46 个用户图标被顶偏需要修复"
变成：

```
Status: Passed              MeasuredDisplays: 3/3
SharedSpacing: True         PhaseVerifiedPerDisplay: True
OwnedShortcutsDeleted: True UserIconsUnchanged: True  UserIconsRepaired: 0 unrepaired=0

\\.\DISPLAY1: spacing=(76,98) phase=(62,2)  horizontal=31 vertical=14 active=49 skippedLandings=0
\\.\DISPLAY2: spacing=(76,98) phase=(74,86) horizontal=14 vertical=19 active=37 skippedLandings=0
\\.\DISPLAY3: spacing=(76,98) phase=(19,70) horizontal=25 vertical=10 active=39 skippedLandings=0
```

- 产品库写入 3 条 Measured 事实（`explorer_grid_measurements`，`observed_at_utc = 2026-10-14T14:53:01.1005103+00:00`）；
  授权表仍为 0 行——实测不等于授权，这是对的。
- `\\.\DISPLAY1` 的相位由旧的 `(2,16)` 变成 `(62,2)`：旧值是在**视图坐标系**里算的，
  `138 mod 76 = 62` 正是主屏图标在屏幕坐标里的相位。
- 清场记录里三个探针 `.url` 都是 `original == observed`（`exact=True`），且 `UserIconsRepaired=0`：
  **写入侧换算也是对的**——上一轮需要修 46 个被顶偏的图标，这一轮一个都没被顶偏。
- 因此 §15（"seed 被 Explorer 放到别的屏"）与 §20.5（DISPLAY2/3 未实测）记录的是**修复前**的事实，
  坐标修好后它们不再是环境限制；`explorer-grid-physical-probe-v1.md` 的历次记录保留为历史。

## 24. 一键回滚：撤销上次整理（2026-10-14）

用户授权（"来吧"，2026-10-14）后实现。撤销的语义是**回到那次整理之前的安全检查点**，而不是把每一步
反向挪一遍——只有前者有可信的证据来源：快照是写盘前真实读到的桌面。

### 24.1 三段式，一步都不能省

1. **只读核对**：`src/DesktopAgent.Core/DesktopLayouts/Restore/Execution/DesktopLayoutRollbackPreflight.cs`
   回答两件事：① 有没有一份可信的"原来长什么样"（已收尾会话 + 同一块屏的安全检查点）；
   ② 桌面现在还是不是我们上次整理完的样子。动过的项目**现在还在不在终点**也要看——停在临时中转位
   同样算"被改过"；对不上就点名（最多 5 个，`MaxNamedChangedItems`）。
2. **还原计划**：`TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, freshRead, displays, capabilities, targetDeviceName)`。
   目标位置全部来自写盘前那份快照；占位阻断、环形依赖、缺失各自单独归类。
3. **受控执行**：与「应用到桌面」**同一个** `TargetDisplayLayoutExecutionCoordinator`、同一道
   `TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch` /
   `TargetDisplayLayoutStagingExecutionScopeGate.Evaluate`。Coordinator 会再读一次桌面、重建 `ExecutionPlan`、
   再跑一遍门，并在第一笔写入前保存并回读安全检查点。

### 24.2 拒绝条件（枚举 + 界面文案的唯一出处）

| 值 | 什么时候 | 界面说什么 |
|---|---|---|
| `NoCommittedSession` | 还没有成功收尾的整理 | 还没有可以撤销的整理 |
| `SessionNotCompleted` | 最近一次执行还没收敛 | 先处理恢复现场 |
| `NothingWasMoved` | 上次整理一项都没挪 | — |
| `SnapshotMissing` / `SnapshotNotForSameDisplay` | 安全检查点丢了 / 不是这块屏的 | — |
| `TargetDisplayMissing` | 拓扑里已经没有那块目标屏 | — |
| `DesktopChangedSinceApply` | 桌面自上次整理后被改过 | 盲目回退会把你的改动一起带走 |
| `NothingToUndo` | 核对通过但已在检查点位置 | 现在就是整理前的样子 |
| `CurrentDesktopUnreadable` | fresh 观察失败 | 读不到当前桌面 · 撤销入口保持关闭 |
| `UnresolvedRecoverySite` | 有未收敛的执行现场（任何一块屏的） | 先点「处理恢复状态」 |
| `RestorePlanBlocked` | 还原计划被占位 / 环 / 缺失挡住 | 撤销停在这里，可以手工处理后再试 |
| `ScopeNotAllowed` | 执行安全门不放行 | 撤销被安全门拒绝 |

### 24.3 服务与查询边界

- `src/DesktopAgent.WindowsInterop/DesktopLayouts/Restore/Execution/DesktopLayoutRollbackService.cs`：
  `PrepareAsync()` 只读；`RollbackAsync()` 串行（`_rollbackLock`）、先看未收敛现场（这一步**连桌面都不读**），
  再用**同一次** fresh 观察同时供判定与计划使用——避免"判定依据"和"执行依据"来自两次不同事实。
  结果里带 `BlockReason`（能撤销或执行完为 null），界面不必去猜原因。
- 查询用新接口 `ICommittedRecoverySessionQuery.ListCommittedAsync`（`SqliteRecoverySessionStore` 一并实现）；
  **没有**往 `IRecoverySessionStore` 加成员——那会打断约 15 个测试 fake 的编译。

### 24.4 产品入口

- `src/DesktopAgent.WinUI/LayoutManagementView.xaml` 的 `RollbackLastApplyButton`（「撤销上次整理…」，
  与「处理恢复状态」同排：两件事都是执行现场操作）。
- `src/DesktopAgent.WinUI/LayoutManagementView.Rollback.cs`：先 `PrepareAsync` → 不通过就说明原因并停住 →
  通过才弹确认框（默认按钮是"取消"）→ `RollbackAsync` → 结果对话框；成功后
  `InvalidateDirectApplyEvidence(...)` 让冻结预览证据失效，逼用户重新预览当前桌面。
- 产品层不许碰 COM 与写入器：由 `ProductRollbackEntryContractTests` 源码级钉住。

### 24.5 测试

- `tests/DesktopAgent.Tests/DesktopLayouts/Restore/Execution/DesktopLayoutRollbackPreflightTests.cs`（11 项，纯判定）
- `tests/DesktopAgent.Tests/DesktopLayouts/Restore/Execution/DesktopLayoutRollbackServiceTests.cs`（11 项：
  未收敛先停且不读桌面、读不到桌面、没有候选、桌面被改过点名、计划被占位、成功只委托一次、NoOp、
  执行失败带原因、`PrepareAsync` 只读）
- `tests/DesktopAgent.Tests/Productization/ProductRollbackEntryContractTests.cs`（4 项源码级边界：
  入口存在、核对必须在写入之前、不碰 COM/写入器、复用同一条受控执行路径）

验证：`dotnet build DesktopAgent.sln -c Debug` 0 错误；全量 `dotnet test` **2429/2429** 通过
（其中本次新增 26 项：前置判定 11 + 服务层 11 + 产品边界 4）。

### 24.6 真机演练记录（2026-10-14 23:11–23:14）

用户选择"现在演练一键回滚"后，在产品面板里跑完了一次真实的应用 → 撤销：

1. **应用**（23:11:35）：选「AI 建议布局」，预览报「可以应用 · 这次预览要移动 43 项」，确认框写明
   目标显示器 `\\.\DISPLAY1`、预计移动 43 个、跨屏迁入 0、执行步骤 43、无临时中转、写入前会建安全检查点。
   确认后：状态条「布局已应用到桌面 · 安全执行与独立验证已完成」。
   产品库新增已收尾会话 `335cdfc6-…`：`forward_attempt_count=43`，43 笔回读**全部** `is_exact=1`，
   独立验证 `FailedItemCount=0`；安全检查点 `c257d085-…` 在写入前建立并回读。
2. **撤销**（23:13:39）：点「撤销上次整理…」，确认框写的是
   「会把 43 个项目放回 `\\.\DISPLAY1` 上整理前的位置（回到安全检查点 `c257d085-…`，取自 2026-10-14 23:11:35）」，
   确认后对话框「已撤销上次整理」。产品库新增会话 `8a43e154-…`（`fwd=43`，`FailedItemCount=0`）——
   **撤销本身也留了一条可再撤销的会话**。
3. **独立核对**（不看产品自己的结论）：把安全检查点里的 144 个位置与撤销后的真机读数逐项比对——
   名称能对上的 **123 项坐标全部一致（0 处不符）**；其余 21 项是 `DisplayName` 与 `stable_id` 的命名差异，
   它们的坐标也**全部**落在检查点位置集合里（0 处不在）。即桌面回到了整理前的那一份摆法。

演练顺带发现并修掉一处产品缺口：撤销完成后页面状态条仍挂着上一条「布局已应用到桌面」，
现在撤销的三条出口（不能撤销 / 成功 / 失败）都会如实改写状态条，并由
`ProductRollbackEntryContractTests` 钉住。

### 24.7 跨屏撤销与「工作区设计应用」的可见性（2026-10-14）

用户在单屏整理之后提出要试**跨屏汇聚**（把三块屏的图标收到一块屏上）。动手之前先补了两处会让"跨屏"
变成单程票的缺口：

**缺口 A：还原计划是逐屏的。** `TargetDisplayLayoutRestorePlanner.CreatePlan`
（`src/DesktopAgent.Core/DesktopLayouts/Restore/TargetDisplayLayoutRestorePlanner.cs`）原先硬性要求
`snapshot.TargetMonitorDeviceName == targetDeviceName`，并且只取快照里属于该屏的项目；跨屏写入意味着
"源屏上的项目"也要挪回去，这张只读快照的目标屏归属会对不上。现在多了一个显式重载
（`allowSnapshotFromAnotherDisplay`），只有撤销路径传 true；"被这次写入动过的屏"由
`DesktopLayoutRollbackRestoreTargetResolver` 从快照里逐项算出来（目标屏排第一，其余按键名序），
撤销按屏循环、每屏各自重新观察一次现场。执行规划器本来就不禁止跨屏移动
（`TargetDisplayLayoutExecutionPlanner` 只对 AlreadyAtTarget 要求显示器一致），这一层不用改。

**缺口 B：工作区设计应用对「撤销上次整理」不可见。** 工作区（设计库 blueprint）这条路径只写
安全检查点快照 + `workspace_execution_journals` / `workspace_dynamic_execution_journals`，
**不写 `RecoverySessionRecord`**；而撤销的候选只查 `ICommittedRecoverySessionQuery`。后果不只是"撤不了"，
而是更糟：它会退回去选中更早的那条恢复会话，语义变成"撤销上一次撤销"。现在：

- 新增窄接口 `ICommittedWorkspaceExecutionJournalQuery`（定义在
  `src/DesktopAgent.Core/DesktopLayouts/Workspaces/Execution/DesktopWorkspaceDynamicExecutionJournal.cs`），
  只由 `SqliteDesktopWorkspaceDynamicExecutionJournalStore` 实现（`lifecycle = Committed`，
  按 `started_at_utc` 降序）。**没有**把成员加到 `IDesktopWorkspace(Dynamic)ExecutionJournalStore` 上——
  那会碎掉三个测试替身和装饰器。
- 撤销候选从"恢复会话"泛化成 `DesktopLayoutRollbackCandidate`（来源枚举 `RecoverySession` /
  `WorkspaceApply`，安全检查点 id、起始时间、逐项 `(StableId, DisplayName, 期望终点)`），两条路径各一个工厂：
  `TryFromRecoverySession`（已完成的执行步骤终点）与 `TryFromWorkspaceApply`（动态 journal 里
  `Authority.ItemGoals` 中 `IsMoveAuthorized` 的目标位置——就是交给写入器的那个物理位置）。
- 服务取二者中 `StartedAtUtc` 更晚的那次；工作区来源会在界面文案里点明（"来源：工作区设计应用"），
  没有单一目标屏时不说"放回 <屏>"，而说"放回这次整理前的位置"。
- 判前条件一条没松：桌面与那次写入的期望终点不完全一致就停手点名，绝不盲目回退。

**测试**：`tests/DesktopAgent.Tests/DesktopLayouts/Restore/Execution/DesktopLayoutRollbackServiceTests.cs`
新增 2 项（工作区那次比更早的恢复会话新时必须选中它；工作区整理之后桌面又被动过时必须停手点名），
以 `Rollback` 为名的 71 项测试全绿。

### 24.8 仍然敞开的

- 「撤销上一次**撤销**」还只能靠连续点两次撤销来达成（每一次撤销都会落一条新会话，能力上已经支持，
  但界面上没有"重做"入口）。
- 不做"只撤销某几个图标"：部分撤销会造出一个没有安全检查点的中间态。
- 撤回窗口没有时间限制：只要桌面还是整理后的样子、检查点还在，多久之后都可以回到那个状态。
- 工作区设计应用仍然**不写恢复会话**：撤销现在能看见它（走 journal 候选），但"处理未收敛的恢复状态"
  那一套界面依然只认恢复会话——两条写入路径的账本还没有真正统一。

### 24.9 跨屏汇聚真机演练：143 项汇到一块屏，再原样放回（2026-10-14 23:42 – 2026-10-15 00:12）

用户要求试"把三块屏都移到一块屏上整理"。跨屏汇聚**只有一条产品路径**：AI 助手页跑一次桌面理解 →
「生成精确布局预览」（冻结预览）→ 布局页「执行」岛 → 应用。设计库里的工作区设计是**只读设计资产**
（详情页明确写"只读 · 无执行权限"），不能直接应用。

**基线（真机观察）**：`\\.\DISPLAY1`(主屏 0,0,2560,1440) 43 项；`\\.\DISPLAY2`(左侧竖屏 -1080,-210,0,1710) 45 项；
`\\.\DISPLAY3`(下方宽屏 309,1440,2229,2520) 56 项；合计 144 项，无图标落在未知屏。

**汇聚**：给 AI 的意图是"三块屏上所有可移动的图标全部集中到下方那块副屏；主屏中央留空；左屏图标也全部迁走；
回收站留在主屏原位"。冻结预览写明：3 块显示器、144 个项目、**移动 143 · 跨屏 88**；明细为主屏 43 项全部迁出、
左屏 45 项全部迁出、下方宽屏**目标 143 项**。应用 23:49:22 开始、23:50:21 收尾，动态 journal
`1496d69b-182b-4db8-ba1a-91a741cf60bb`（`lifecycle=Committed`、`move_goal_count=143`、
`last_observed_satisfied_goal_count=143`、独立验证通过），写入前安全检查点
`b717e9b7-14fc-4687-aa5d-709ad493f493`（全桌面 144 项，`target_monitor_device_name=NULL`）。
应用后真机：主屏 0 / 左屏 0 / 下方宽屏 **144**——汇聚确实发生在真机上，不是日志里的说法。
（回收站没有被搬：它的 `Place` 目标就是原位，`is_move_authorized=false`。）

**撤销（三次点击，暴露三处缺陷）**

1. **规划器拒绝全桌面检查点**（23:52）：点「撤销上次整理…」报
   「按安全检查点生成还原计划失败 — `\\.\DISPLAY1`：快照的 `TargetMonitorDeviceName` 为 null，
   不能用于指定显示器恢复」，**桌面未动**（fail-closed 生效）。根因：工作区路径写的是**全桌面**检查点，
   目标屏为 null，而 §24.7 新增的重载只放开了"跨屏"这一层，没放开 null 目标屏。
   修法：`TargetDisplayLayoutRestorePlanner` 里那道 null 校验只在**不**允许跨屏时生效。
2. **已在原位的回收站被当成"挡路的系统图标"**：修掉 1 之后，主屏 43 项、左屏 45 项都还原成功，
   收尾在下屏被挡下——「这一屏没有移动图标（已还原 `\\.\DISPLAY1`、`\\.\DISPLAY2`）
   [当前图标 "回收站" 不是文件系统项目。]」。根因：`TargetDisplayLayoutRestorePlanner` 先判
   「非文件系统项目 / 系统项目 / 无显示器」再判「已经在目标位置」，于是"本来就在原位、根本不需要动"
   的回收站成了阻塞项。修法：把 `AlreadyAtTarget`（位置与显示器都一致）提到这些校验之前。
   测试：`NonFileSystemItem_AlreadyAtTarget_IsNotBlocked`、`SystemItem_AlreadyAtTarget_IsNotBlocked`、
   `NonFileSystemItem_NeedsMove_StillBlocked`。
3. **撤销文案点错了屏**：上面两次撤销各自留下新会话，于是第三次点撤销时，候选换成了**上一次撤销自己**
   写下的那条会话。确认框写「会把 45 个项目放回 `\\.\DISPLAY2` 上整理前的位置」——而那次写入的落点
   其实在 `\\.\DISPLAY3`（它把 45 项从 DISPLAY2 挪到了 DISPLAY3）。用户按文案理解会以为"回左屏"，
   实际发生的是"回到汇聚状态"。根因：文案用的是**会话自己的目标屏**（那次整理写到哪），而撤销的落点是
   **检查点里这些项目所在的屏**。修法：`DesktopLayoutRollbackPreflight` 从快照逐项算出落点屏——
   一块屏就写屏名，多块屏就写「涉及 N 块屏：…」，并给候选目标屏优先排序。
   测试：`ApproveText_NamesTheCheckpointDisplay_NotTheRecordedTargetDisplay`、
   `ApproveText_CrossDisplayUndo_NamesEveryCheckpointDisplay`。

**收尾（把桌面真的放回去）**：修完 3 之后，桌面处于"88 项已回家、下屏 55 项仍在汇聚位"的中间态，
而候选判定会正确地拒绝它（桌面与那次写入的终点已不一致 → 「桌面已经被改过 · 不盲目回退」，
这是设计意图，不是缺陷）。于是用**与产品同一套执行协调器**（`TargetDisplayLayoutExecutionCoordinator`
+ 安全快照 + 恢复会话 + 作用域门 + 独立回读验证）按检查点 `b717e9b7` 单独还原下屏：
55 项移动、`status=Completed`、`is_success=true`、`verification_status=Passed`、
`verification_failed_items=0`、写入前安全快照 `af061286-aef3-4d37-b634-5e7434883fa7`。

**独立核对**（不看产品结论，直接读真机 + 只读打开检查点）：144 项**全部**落在检查点位置上——
`match_same_display_same_position=144`、`same_display_other_position=0`、`display_mismatch=0`、
`missing_now=0`、`live_only=0`；三屏回到 43 / 45 / 56。桌面确实回到了汇聚之前的样子。

**仍然敞开（下次要做的事）**：撤销的候选是"最近一次写盘"，**不区分**那次写盘是整理还是撤销，
所以"撤销 → 再撤销"会变成"重做"（语义上自洽：撤销上一次移动；但按钮叫"撤销上次整理"，
用户可能期待第二次点击没有东西可撤）。干净的修法是给写入记**来源**（整理 / 检查点还原）：
`desktop_layout_recovery_sessions` 加一列（schema v13，`origin`），执行协调器落库时带上，
候选查询只认整理来源。这需要动共享 schema 与恢复会话聚合；2026-10-15 用户点头后已在 **§25** 落地。


## 25. 撤销的来源标记（schema v13）：撤销自己写下去的那一笔不再是候选（2026-10-15）

**问题**（§24.9 末尾留下的洞）：撤销候选是"最近一次写盘"的整理会话，**不区分**那次写盘是
用户的整理，还是撤销自己按检查点做的还原。于是"撤销 → 再撤销"会读到自己刚写的那一笔，
语义上变成"重做"。

**决策**：给恢复会话记**写入来路**，并让候选查询只认整理来源——而不是在界面上做启发式判断。
写入只有两个入口，所以枚举只有两个值：

| 写入口 | origin |
| --- | --- |
| `TargetDisplayLayoutExecutionCoordinator`（产品整理／跨屏汇聚／校准回放） | `organize` |
| `DesktopLayoutRollbackService`（按检查点还原，即"撤销"） | `checkpoint-restore` |

**契约层**（`src\DesktopAgent.Core\DesktopLayouts\Restore\Execution\`）：
`RecoverySessionRecord.cs` 新增 `public enum RecoverySessionOrigin { Organize = 0, CheckpointRestore = 1 }`
与 `Origin { get; init; } = RecoverySessionOrigin.Organize`（默认值让所有既有构造点保持"整理"语义）；
`TargetDisplayLayoutRecoverySession.cs` 的 `Origin` 是只读属性，构造参数默认 `Organize`；
`ICommittedRecoverySessionQuery.cs` 把"只认整理来源"写进接口契约：撤销自己写下的那一笔
（`checkpoint-restore`）永远不是候选。

**执行层**（`src\DesktopAgent.WindowsInterop\DesktopLayouts\Restore\Execution\`）：
`TargetDisplayLayoutExecutionCoordinator.cs` 的
`ExecuteWithScopeGateAsync(..., RecoverySessionOrigin origin = RecoverySessionOrigin.Organize)`
转发给 `ExecuteCoreAsync(..., RecoverySessionOrigin origin)`（必填参数，防止将来新增入口漏传）；
落库带 `origin: origin`；续跑/重建路径一律沿用 `original.Origin`，不重新猜来路。
`DesktopLayoutRollbackService.cs` 撤销走同一个协调器，显式传
`RecoverySessionOrigin.CheckpointRestore`——"撤销复用产品同一条执行路径"的既有契约测试继续成立。

**持久化层**（`src\DesktopAgent.Persistence\`）：
`Database\SqliteDesktopAgentSchema.cs` 的 `CurrentVersion = 13`，`EnsureV13Schema` 对
`desktop_layout_recovery_sessions` 执行
`ALTER TABLE ... ADD COLUMN origin TEXT NOT NULL DEFAULT 'organize' CHECK(origin IN ('organize','checkpoint-restore'))`，
带 `ColumnExists` 幂等守卫（V4 建表语句不含 `origin`，所以新库也是走这条升级路径）。
`DesktopLayouts\SqliteRecoverySessionStore.cs`：SELECT 多读一列（`ParseOrigin` 对未知值抛
`会话 {id} origin 值无效：{value}。`，不静默降级）、INSERT 多写 `@origin`；
`ListCommittedAsync` 收紧为 `WHERE lifecycle = $lifecycle AND origin = $origin ORDER BY rowid`。

**界面**：文案不动。"撤销上次整理"现在名副其实——候选集合里不再有撤销自己写下的行。

**旧行回填的边界（诚实记录）**：升级前写下的行一律回填成 `organize`，包括 v13 之前由"撤销"
入口写下的那几笔（当时库里没有来路信息，无法事后区分）。它们仍是候选，但位置检查
（§24.2 的拒绝条件）继续挡住"桌面与那次写入终点不一致"的情况，所以不会盲目回退。

**测试**：
- `SqliteDesktopAgentSchemaTests.V13Upgrade_AddsOriginColumnAndBackfillsExistingSessions`：
  把会话表换回缺列形态 + `user_version = 12`，初始化后断言 13、`origin` 列存在、
  既有行回填 `organize`、非法 `origin` 被 CHECK 拒绝。
- `SqliteRecoverySessionStoreTests.SaveAsync_AndGetAsync_RoundTripTheWriteOrigin`：两个来路都能落库读回。
- `SqliteRecoverySessionStoreTests.ListCommittedAsync_ExcludesSessionsWrittenByCheckpointRestore`：
  更"新"的 `checkpoint-restore` 会话（-5 分钟）不能挤掉更旧的整理会话（-30 分钟）。
- `SqliteDesktopWorkspaceDynamicExecutionJournalStoreTests`、`SqliteDesktopAgentSchemaTests` 等
  所有硬编码版本号的断言一并升到 13（全量 2442 项通过，其中 origin 相关新增 3 项）。

**真实产品库迁移验证（2026-10-15，Dev 渠道实际库的副本）**：把本次跨屏演练留下的真实库
`%LocalAppData%\Tessel.Dev\desktop-agent.db`（1 863 680 字节，`user_version = 12`）整份复制到临时目录，
用产品自己的 `SqliteRecoverySessionStore.InitializeAsync` 跑一次初始化，再读回：

| 观察点 | 迁移前 | 迁移后 |
| --- | --- | --- |
| `PRAGMA user_version` | 12 | 13 |
| `desktop_layout_recovery_sessions.origin` 列 | 不存在 | 存在 |
| 恢复会话行数 | 11（lifecycle 2=10、6=1） | 11（未丢行） |
| `origin` 分布 | —— | `organize=11`（旧行一律按上文边界回填） |
| 其它业务行（journal 1、网格实测 5、屏授权 3） | 1 / 5 / 3 | 1 / 5 / 3 |
| `PRAGMA integrity_check` | ok | ok |
| `ListCommittedAsync(50)` | —— | 10 条（只含已收尾的整理会话，lifecycle=6 那条不入选） |

**真机读写（2026-10-15 完成，详见 §26）**：产品壳里点"撤销上次整理"写下的那一笔现在确实带
`checkpoint-restore`（rowid 15、`145b1c47`、DISPLAY1、26 步），再点一次被位置守卫挡下并提示
"已经回到那次整理之前"，没有写盘。这一步同时抓出并修掉了 `Origin` 没能一路带进恢复会话记录的缺陷（§26.4）。

## 26. 端到端监督演练（2026-10-15，真机）

**目的**：§2 四条验收标准里唯一没在真机跑通过的一条——把"逐屏站定授权 → 无人值守执行"这条链
走完，并顺带补掉 §25 末尾留下的真机读写验证。

**§26.1 授权面（只读）**：面板"已记住的显示器…"读出三块屏，与库里
`explorer_grid_display_authorizations` 的三行 `state = 0` 一致：

| 对话框里的行 | 库里（`\\.\DISPLAYn`，同一拓扑指纹） |
| --- | --- |
| `\\.\DISPLAY1 · 记住于 2026-10-14 23:50` | `2026-10-14T15:50:21.73+00:00` |
| `\\.\DISPLAY2 · 记住于 2026-10-14 23:50` | `2026-10-14T15:50:21.74+00:00` |
| `\\.\DISPLAY3 · 记住于 2026-10-14 23:50` | `2026-10-14T15:50:21.75+00:00` |

对话框原文："这些显示器在当前显示设置下已经允许无人值守执行。取消勾选＝收回授权，之后 Tessel
会重新先给你计划、等你确认。收回授权不会移动任何桌面"。三个复选框都是勾选态，本次**没有改动**
（只是读）。这三行是 10-14 那次跨屏汇聚应用时勾出来的，说明"逐屏站定授权"在真机上确实发生过。

**§26.2 面板阶段模型（读代码得到的事实）**：工作台三栏按阶段分配——
`src\DesktopAgent.WinUI\LayoutManagementView.Stages.cs:80-87` 把中列（画布 + 预览/应用到桌面）
只归"执行"阶段，右列（编辑器）归"设计"阶段；所以"预览/应用到桌面"按钮**只在执行阶段存在于 UI 树里**。
这一条在演练前并不知道，是驱动 UIA 时找不到按钮后读代码才确认的（面板的 L1/L2 两节没写这一步）。

**§26.3 真机执行链（修复后的构建）**：

| 步骤 | 观察 | 证据 |
| --- | --- | --- |
| 设计阶段选"AI 建议布局 · DISPLAY1"（`2d60bc6d`，V1 · 5 区）→ 执行阶段 | 画布出现，就绪条"还不能应用 · 没有预览" | UIA 读 `ApplyReadinessHeadlineText` |
| 点"↻ 预览" | "可以应用"、"这次预览要移动 26 项。点击后会重新 fresh 验证，再交给安全执行层写入。"、移动 26、目标屏 `\\.\DISPLAY1` | 同上 + `MoveCountText`/`TargetDisplayText` |
| 点"应用到桌面"→ 确认框 | 标题"应用布局到真实桌面？"，正文"布局：AI 建议布局 · DISPLAY1 / 目标显示器：`\\.\DISPLAY1` / 预计移动：26 个图标 / 其中跨屏迁入：0 个。" | UIA dump |
| 确认 | "应用流程已结束 · 请重新预览当前桌面"，随后弹出"布局已应用到桌面" | 同上 |
| 落库 | rowid 14、`2fddfc8d`、`\\.\DISPLAY1`、lifecycle 2、**origin = organize**、23:16:18 → 23:16:24 | `sqlite3` 只读查产品库 |

**§26.4 抓到的真缺陷：`Origin` 没落到库**。修复前的构建里做同一条链（应用 → 撤销）得到：

| 行 | 会话 | 目标屏 | origin | 时刻 | 说明 |
| --- | --- | --- | --- | --- | --- |
| 12 | `bb48922c` | `\\.\DISPLAY1` | `organize` | 23:10:00 → 23:10:06 | 应用（正确） |
| 13 | `a33b28c3` | `\\.\DISPLAY1` | **`organize`** ✗ | 23:11:18 | 撤销写下，却记成了整理 |

也就是说：撤销服务确实把 `RecoverySessionOrigin.CheckpointRestore` 交给了执行器，但**执行器在重建
会话记录时又把它丢了**，落库永远走默认值。根因是两处对象重建（`TrySaveInitialCheckpointAsync`
与 `TryUpdateAndGetVerifiedAsync`）以及 `RecoveryOrchestrator.PersistRecoveryResultAsync` 都没有
拷贝 `Origin`：

- `src\DesktopAgent.WindowsInterop\DesktopLayouts\Restore\Execution\TargetDisplayLayoutExecutionCoordinator.cs:1594-1599`
- `src\DesktopAgent.WindowsInterop\DesktopLayouts\Restore\Execution\TargetDisplayLayoutExecutionCoordinator.cs:1652-1656`
- `src\DesktopAgent.Core\DesktopLayouts\Restore\Execution\RecoveryOrchestrator.cs:104-108`

三处各加一行 `Origin = session.Origin` / `Origin = record.Origin` 后，被改写的行（UPDATE）不碰
`origin`（它只在 INSERT 时写入），语义不变。

**守卫测试**：`tests\DesktopAgent.Tests\DesktopLayouts\Restore\Execution\RecoverySessionPersistenceWiringTests.cs`
新增 `CheckpointRestoreOriginReachesTheStoredRow`——用 `ExecuteWithScopeGateAsync(..., RecoverySessionOrigin.CheckpointRestore)`
跑完一次真实协调器，断言落库记录的 `Origin` 是 `CheckpointRestore`，并且 `ListCommittedAsync` 为空
（撤销写下的那一笔不在候选里）。全量 **2443 / 2443** 通过。

**§26.5 再撤一次被挡**：修复后的构建里，撤销完成后再点"撤销上次整理…"，弹出"现在还不能撤销"：
标题"已经回到那次整理之前"、正文"上次整理写过的 26 个项目现在都在安全检查点位置上，没有需要再撤
一次的东西。"；库里行数没变（COUNT = 15、MAX(rowid) = 15），即**没有写盘**。撤销前那个确认框的
文案也对得上："会把 26 个项目放回 `\\.\DISPLAY1` 上整理前的位置（回到安全检查点 bada95d9-a321-405d-947a-8b0be…）"。

**§26.6 诚实边界**：
1. "无人值守"这一侧仍然默认关闭（`DesktopAutomationUnattendedGate` 与
   `DesktopAutomationRuntimePolicy.CanAttemptUnattendedExecution` 都是默认关），本次验证的是
   **授权事实存在、证据门与授权门分得开、监督路径可用**，不是"机器会自己动"。
2. 直接应用（画布）路径的确认框里**没有**"记住这些显示器"复选框，那个复选框在工作区/AI 路径上；
   本次直接应用因此没有、也不需要写授权。
3. v13 之前的旧行仍按 §25 的边界一律是 `organize`（本次演练里 rowid ≤ 13 的行就是这样），
   它们继续靠位置守卫挡住盲目回退。

## 27. 无人值守点火演练（2026-10-15）：钥匙能开门，但两半接不上

**目的**：§26 只跑到"监督路径可用"，这一节要真回答一个问题——**把"无人值守"那扇门打开，这台
机器今天能不能自己动**。做法是：先按 M3-B07 的点火钥匙武装一次 PoC，再用产品自己的代码把
"闸门判定"和"每块屏的 clearance"逐条打出来。

**§27.1 引擎和钥匙在哪（事实，grep + 读代码）**

- 无人值守引擎**只在 PoC**：`src\DesktopAgent.Poc\App.xaml.cs:247-260`——`m3AutomaticAcceptance.IsEnabled`
  为真时用 `CreateValidatedRuntimePolicy`，否则用 provisional-closed 的默认策略；安全门
  `DesktopAutomationAutomaticSafetyGate`、`DesktopAutomationCoordinatorExecutionBridge`、
  `DesktopAutomationExecutionCycleCoordinator` 都只在这个文件里注册。对 `DesktopAgent.WinUI`
  全目录 grep `UnattendedGate / DesktopAutomationRuntimePolicy / AutomaticSafetyGate / ValidatedOpen /
  Unattended`：**零命中**——产品壳里根本没有这套引擎，产品里的"自动化模式"只是一个被存下来
  显示出来的设置（`DesktopProductSettings` / `MainWindow.Overview` / `OverviewLightweightReadModel`），
  "待确认"页只把原因翻译成文案（`PendingActionListItem.FormatQueueReason`）。
- 点火钥匙（Debug-only，`#if DEBUG`）是五个环境变量：`DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MODE=ValidatedOpen`、
  `..._ARM=I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITES`、`..._MAX_MOVES=1`、`..._MAX_STEPS=1`、
  `..._ALLOW_STAGING=false`；任一缺失/不合规 → 启动即 fail-closed 退出；M2 的
  `DESKTOPAGENT_ACCEPTANCE_FAULT_MODE` 与它互斥。文档 `docs\experiments\M3-B07-controlled-automatic-acceptance.md`
  的"三重武装"还包括 Debug 启动时点 Yes、以及启动后从 **托盘**手动切 Automatic。

**§27.2 两套 runtime 的闸门（用产品自己的类型实测，都请求 Automatic）**

| runtime | `UnattendedGate.Reason` | Requested | Effective | `CanAttemptUnattendedExecution` |
| --- | --- | --- | --- | --- |
| 默认（未武装） | `M2RealDualMonitorAcceptancePending` | Automatic | **Suggestion** | false |
| 点火钥匙已武装 | （无） | Automatic | **Automatic** | **true** |

**§27.3 每块屏的 clearance（用产品自己的 `DesktopGridDisplayClearanceProvider`，两个真库各跑一遍）**

| 库 | `\\.\DISPLAY1` | `\\.\DISPLAY2` | `\\.\DISPLAY3` |
| --- | --- | --- | --- |
| 产品库 `%LocalAppData%\Tessel.Dev\desktop-agent.db` | `Measured` / 授权 true | `Measured` / 授权 true | `Measured` / 授权 true |
| PoC 库 `%LocalAppData%\DesktopAgent\desktop-agent.db` | `Measured` / 授权 **false** | `Unknown` / 授权 false | `Unknown` / 授权 false |

**§27.4 结论：卡在"接线"，不是"钥匙"**

1. 钥匙有效：武装后 runtime 真的进入 `EffectiveMode = Automatic`、`CanAttempt = true`。
2. 但**站定授权只有产品侧能签发**：唯一的签发入口是产品工作区应用成功后的
   `LayoutManagementView.WorkspaceApply.cs`（复选框"在这套显示设置下记住参与设计的 N 块显示器，
   之后允许无人值守执行"→ `DesktopGridDisplayAuthorizationGrantService.GrantAsync`）。PoC 里对
   `GrantAsync` / 授权写入的调用：**零**（PoC 只注册 `IExplorerGridDisplayAuthorizationStore` 去读）。
   PoC 自己的注释也写着"站定授权由用户确认界面的'记住这块屏'写入"——那个界面在 PoC 里从未实现。
3. 又因为**两边是两套库**（§18），产品里点出来的授权落在 `Tessel.Dev` 库里，PoC 的闸门读的是
   `DesktopAgent` 库：于是"钥匙开了门，门后仍然没有合法授权"。武装启动的 PoC 跑完整个演练**没有
   写任何东西**——它的库连 schema 都没升（仍是 `user_version = 11`、0 条恢复会话），桌面一步未动。
4. 操作层面还有一个卡点：本工具的 UIA 驱动**枚举不到 Win11 托盘图标**（`Shell_TrayWnd` 子树里
   0 个按钮、隐藏图标区未创建），而 `M3TrayController.cs:264` 的 `SetRequestedMode` 是 PoC 里
   **唯一**的模式开关——所以即便是正面场景，也该由人工在托盘上点（或用别的注入方式），不宜算作
   自动化步骤。

**§27.5 三条备选接线（都需要产品决策，本次未做）**

1. **把引擎装进产品**：产品才有签发面，引擎和授权自然在同一侧、同一个库。代价最大，但这是唯一
   让"无人值守"在产品里成立的路。
2. **让引擎读有授权的库**：把 PoC/引擎的数据目录做成可配置（或让它读产品库），于是"人在产品里
   记住这块屏 → 引擎据此执行"。代价小，适合先验证引擎行为；但要注意这等于承认"授权与执行可以
   分属两个进程、两个库"。
3. **在 PoC 侧补一个与产品等价的确认面**：让 PoC 也能"记住这块屏"。代价中等，但它会把"站定授权"
   的签发面复制成两份，和 §16 的设计意图（授权只从用户确认界面来）需要重新对齐。

**§27.6 诚实边界**：本节验证的是**闸门判定与接线缺口**（用产品自己的类型与真库跑出来的），
不是"无人值守跑通了"；本次没有任何自动执行发生（两个库的行数都没变）。托盘那条结论是本机
Windows 11 隐藏图标区的操作限制，换注入方式可能不同。

## §28 数据目录可配 + 验收 harness 点火演练（2026-10-15，真机）

**§28.1 落地：数据根目录可配置（§27.5 备选 2）**

- `src\DesktopAgent.Persistence\DesktopLayouts\DesktopAgentDataPaths.cs` 新增
  `public const string RootDirectoryEnvironmentVariable = "DESKTOPAGENT_DATA_DIR";` 与
  `public static string ResolveRootDirectory(string? overrideDirectory)`（空白 →
  `%LocalAppData%\DesktopAgent`；非空 → `Path.GetFullPath`，所以相对路径也接受）。
  `DefaultRootDirectory` 读这个环境变量，`DefaultDatabasePath` / `DefaultSettingsPath` /
  `DefaultSecretsDirectory` / `DefaultRadarCachePath` 四个派生路径随之整体搬家。
- 产品壳不受影响：它走自己的 `ProductDataPathFacade` / `TesselProductDataPaths`（`Tessel.Dev`
  渠道目录），这是 grep 确认过的（产品壳里对 `DesktopAgentDataPaths` 的引用只出现在别名门面里）。
- 测试 `tests\DesktopAgent.Tests\Persistence\DesktopAgentDataPathsTests.cs`：4 个方法 / 6 个用例
  （override 生效、空白回退、派生路径跟随、常量名稳定）。
- 全量：`dotnet test tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Debug -p:Platform=x64`
  → **2449 / 2449 通过**（比 §26 的 2443 多这 6 项）。

**§28.2 真机证明：引擎确实读了另一份库**

- 演练库 `G:\work\LAB\drill-unattended\desktop-agent.db` 是产品库的副本：v13、3 条站定授权
  （`\\.\DISPLAY1/2/3`，`state=0`）、5 条实测证据、5 个模板。
- 启动器 `G:\work\LAB\drill-unattended\launch-armed.cmd`（纯 ASCII）：必须在 cmd 自己的进程里
  `set`，因为 harness 的 `Start-Process` 不会把环境变量传给孙进程（实测 `cmd /c set FOO > file`
  读到空）。PoC 的真实输出目录是 `src\DesktopAgent.Poc\bin\x64\Debug\net10.0-windows10.0.19041.0\`
  （`AssemblyName = DesktopAgent.Diagnostics`；`bin\Debug\net10.0-windows\DesktopAgent.Poc.exe`
  是 2026-08-12 的陈旧残留，早先的尝试跑的就是它）。
- 证据一：工作台状态栏 `StatusText` = 「数据库路径：`G:\work\LAB\drill-unattended\desktop-agent.db`」。
- 证据二：UIA 点 `ReadIconsButton` → `SaveSnapshotButton` → 演练库 `2 105 344 → 2 125 824` 字节
  （11:11:34），旧库 `%LocalAppData%\DesktopAgent\desktop-agent.db` 未被触碰（11:01:19）。

**§28.3 验收 harness 新增一个"初始请求模式"选项**

托盘这条注入路在本机仍然走不通：主任务栏是**左侧竖条**（`Shell_TrayWnd` (0,0,124×1440)），
可见托盘里没有 PoC 图标（UIA 在 `Shell_TrayWnd` 子树里读不到图标）；`NotifyIconOverflowWindow`
（`(124,1139) 240×200`，内部 `ToolbarWindow32` 26 个按钮）里只有**产品**的托盘图标——溢出网格
c4r3 (304,1279) 的菜单是「打开 DesktopAgent / 布局 / 待确认 / 设置 / 退出」，没有模式项；
`M3TrayController.cs:264` 的 `SetRequestedMode` 仍是 PoC 里唯一的模式开关。

- 于是给 Debug 验收 harness 加一个显式选项 `DESKTOPAGENT_M3_AUTO_ACCEPTANCE_REQUESTED_MODE`
  （`src\DesktopAgent.Poc\Acceptance\M3AutomaticAcceptanceOptions.cs`；默认仍是 `Suggestion`，
  只接受 `Automatic` / `Suggestion`），作为 `DesktopAutomationRuntimePolicy` 的 `initialMode`
  （构造器 `src\DesktopAgent.Application\DesktopAutomation\DesktopAutomationRuntimePolicy.cs:220-233`
  的 `initialMode` 参数）传进去；武装对话框现在会把实际请求模式打出来。
- 它只是"等价于用户从托盘点 Automatic"的一次输入：安全边界（ValidatedOpen gate、逐屏实测证据、
  站定授权、风险预算）一个字没松，`SetRequestedMode` 本身也没有额外的安全门。
- 真机过程：武装启动（PID 33576，`launch-armed-auto.cmd`）→ 对话框 Yes → 绑定模板
  「AI 建议布局」（Revision 1）→ 启动后台监听（FileSystem watchers 2 / PeriodicFallback 15s）
  → 在 `C:\Users\29719\Desktop` 新建一个文件，制造一次真实变化。

**§28.4 结果：建议出来了，但一次自动执行都没发生**

- 建议确实生成：`当前 Suggestion：Active | Template=6f0daaa5-4b3f-44a0-9021-fea1acc67cde |
  Target=\\.\DISPLAY1 | Moves=15 | Steps=15 | Staging=False | SourceObservation=3db5fc12-…`；
  后台每 15 秒一轮，`Status: NoChanges`，观测 145 个图标（144 + 新文件）。
- 反证：演练库会话数仍是 **15**、`desktop_pending_actions` **0 行**、库文件 mtime 停在 11:11:34；
  诊断日志（`DiagnosticsText`）里只有 suggestion binding / notification reconcile / background
  trigger 记录，**没有任何 automatic / safety gate / cooldown 记录**。点「模拟打开」
  （`SimulateNotificationOpenButton`）也不改变结果，`NotificationSchedulerStatusText` 一直是
  `NoSinkWork / AlreadyPublished`。
- 原因（读代码得）：自动触发器只在 `src\DesktopAgent.Poc\M3TrayController.cs` 三处被"踢"——
  `:266`（模式切换到 Automatic）、`:291`（Automatic 下 resume）、`:332`（Automatic 下重置熔断），
  都是 `_automaticTrigger.RequestCurrent()`。**启动时就已是 Automatic 不构成一次踢**：`:148` 的
  `Start()` 只启动协调器、不请求当前建议；`StopMonitoringButton` / `StartMonitoringButton`
  （`:354` / `:383` / `:402`）也只是 StopAsync / Start。托盘既然不可达，这条链就没有任何一次点火，
  所以引擎（虽然 `EffectiveMode = Automatic`、`CanAttempt = true`、clearance 也全绿）一直在等
  一个永远不会到来的事件。

**§28.5 诚实边界与下一步**

- 到这一步为止：**钥匙、门、库都对上了，缺的是"点火的那一下"**。这不是授权问题（演练库里三块屏
  的实测证据与站定授权在武装进程里都读得到），而是验收 harness 的事件模型问题。
- 最小下一步（需要产品决策，本次未做）：在 PoC 启动完成、且建议变为 Active 而当前模式是
  Automatic 时补一次 `RequestCurrent()`；或给验收 harness 一个显式的"请求当前建议"入口。
  两者都只是"让无人值守在无托盘交互时可验收"，不改变任何安全边界。
- 本次真机没有发生任何不期望的写入：武装进程全程只写了演练库的一次快照（§28.2 证据二），
  桌面图标一步未动，结束后武装进程已停止、临时文件已删除。

## §29 补点火 + 把"为什么没动手"变成可审计：真机结果与一个独立缺陷

### §29.1 改了什么

- **点火**：`src\DesktopAgent.Poc\M3AutomaticExecutionTriggerCoordinator.cs` 新增
  `RequestCurrentIfAutomatic()`——只有 `_started`、`RequestedMode == Automatic`、
  `EffectiveMode == Automatic` 同时成立才调 `RequestCurrent()`；它与托盘那三处调用**共用同一个
  去重键**（`SuggestionId + LifecycleRevision + RuntimeRevision + EffectiveMode`），因此不会把
  同一条建议执行两次。调用点在 `src\DesktopAgent.Poc\M3TrayController.cs` 的 1 秒状态定时器
  （`OnStatusTick`）里。它覆盖的是"进程启动时模式已经是 Automatic、而建议已经 Active"这一种启动形态。
- **可审计**：新增 `src\DesktopAgent.Poc\M3DiagnosticsWindow.Automation.cs`（partial 扩展：
  `AppendAutomaticCycleResult` / `FormatAutomaticCycleResult`），并由托盘完成回调
  `OnAutomaticCycleCompleted` 落一条 `[B05] automatic cycle …` 到诊断日志（`DiagnosticsText`），
  内容 = `DesktopAutomationExecutionCycleResult` 的 Status / Runtime（模式、gate、暂停、cooldown、
  熔断）/ Preflight（含风险评估与 `blockReasons`）/ Handoff / SafetyGate / Outcome / Fault。
  周期可能在 preflight、handoff、安全门的任意一段原地停止，而托盘只显示一行状态：无人值守时
  日志是唯一能回答"卡在哪一段、为什么"的地方。
- **武装**：`G:\work\LAB\drill-unattended\launch-armed-auto10.cmd`（`DESKTOPAGENT_DATA_DIR` 指向
  演练库、`DESKTOPAGENT_M3_AUTO_ACCEPTANCE_REQUESTED_MODE=Automatic`、`MAX_MOVES=10` /
  `MAX_STEPS=20`，环境证据写 `armed-env-auto10.txt`）。武装对话框 Yes 后进程
  `DesktopAgent.Diagnostics`（本次 PID 44000）启动。

### §29.2 真机结果：点火链通了，每次都被风险预算挡下（带名字）

- 制造一次真实桌面变化后，建议被生成并按预期进入自动周期，日志给出裁决：
  `[B05] automatic cycle PreflightStopped | mode=Automatic/Automatic
  gate=DesktopAutomationUnattendedGate { IsOpen = True, Reason = None } paused=false cooldown=false
  fuse=false | suggestion=c2aeaad0-c86d-49e0-aa56-6f28537e603e lifecycleRev=5 |
  preflight=RiskBudgetBlocked riskMoves=15/10 riskSteps=15/20 staging=false withinBudget=false
  blockReasons=MoveCountLimitExceeded`
- 换绑「新布局」（`db9fb1c7-a848-44f2-b9bc-a0bc4645cfbd`）后：
  `riskMoves=43/10 riskSteps=43/20 … blockReasons=MoveCountLimitExceeded,ExecutionStepLimitExceeded`。
- 实测五个模板（`SuggestionTemplateComboBox` 索引 0–4）：`6f0daaa5`「AI 建议布局」=15 项、
  `16672834`/`2d60bc6d`「AI 建议布局 · DISPLAY1/DISPLAY2」、`762072b1`、`db9fb1c7`「新布局」=43 项。
  改绑定会退休当前建议（`当前 Suggestion：无。最近退休：BindingChanged。`），需要一次新的桌面
  变化才会重新生成建议——这也解释了 §28.4 里"改了绑定就没有建议了"。
- 结论：**引擎会自己醒来、评估、给出裁决**；在今天的桌面上每个候选都超过勾选时的 10 项验收
  护栏，所以它每次都停在写入之前。**没有放宽这个护栏**（那是改靶子）。要观察一次真实写入，
  必须先让桌面与某个已绑定模板一致（监督应用一次），再制造小于预算的漂移。

### §29.3 一个独立缺陷：点「应用到桌面」把产品壳打崩了（本次未修）

- 走"先监督应用一次"这条路需要产品面板的执行阶段：`LayoutTemplateListItem`（`6f0daaa5`）→
  `使用` → `执行`（`StageExecuteButton`）→ `↻ 预览`（`PreviewLayoutButton`）。预览结果是好的：
  `MoveCountText=15`、`StayCountText=29`、`TargetDisplayText=\\.\DISPLAY1`、
  `ApplyReadinessHeadlineText=可以应用`、`ApplyReadinessDetailText=这次预览要移动 15 项。点击后会
  重新 fresh 验证，再交给安全执行层写入。`
- 点 `ApplyToDesktopButton`（「应用到桌面」）后**产品壳进程崩溃退出**：
  - `Application Error`（事件 1000，2026-10-15 14:03:10）：`Faulting application name:
    DesktopAgent.WinUI.exe, version: 1.0.0.0`，`Faulting module name: Microsoft.UI.Xaml.dll,
    version: 3.2.3.0`，`Exception code: 0xc000027b`（stowed exception），
    `Fault offset: 0x00000000003a9dbd`，`Faulting process id: 0x8A7C`；
  - WER `Report.wer`（bucket `1754543713367589833`）：`combase.dll 10.0.26100.9549`、
    `异常代码 80000019`；
  - 可执行文件：`src\DesktopAgent.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\DesktopAgent.WinUI.exe`。
- **失败是 fail-closed 的**：产品库会话数仍是 15（没有新会话）、没有新动态 journal、桌面图标一步
  未动；崩溃前只写了 `settings.json`（14:02:22）。也就是说这条路径没有造成不期望的写入，但也
  没法用它把桌面摆成模板形状。
- 未做：根因定位与修复。`0xc000027b` 是 WinRT stowed exception（典型来源是 `async void` 事件
  处理器里逃出来的异常），落在 XAML 的"应用"路径上。它与本次 ① 的改动无关——运行中的二进制是
  11:01 之前构建的，早于本目录下任何 ① 改动——但它是"监督应用一次"的拦路石。

### §29.4 诚实边界

- 「点火」有证据（上面的 `[B05]` 日志），但当前证据**无法把这次请求归因到"定时器补踢"还是
  "建议事件订阅"**：两条路共用同一个去重键，日志不区分。补踢真正覆盖的是"启动即 Automatic +
  建议已 Active"这种启动形态，并有单元测试守住它的门（只在 `RequestedMode == Automatic` 且
  `EffectiveMode == Automatic` 时踢）；去重保证即使事件订阅已经请求过，也不会重复执行。
- 真实无人值守写入本次**没有发生**：被风险预算（15 项 > 10 项）挡在写入之前，随后监督应用这条
  路又被 §29.3 的崩溃挡住。到这一步，可信执行器这条链上"证据、授权、门、裁决"都有真机证据，
  缺的是"候选小于预算"的一个真实场景。






### §30 监督应用成功；一次真实无人值守写入仍被预算挡住（2026-10-15 14:11–14:15 真机）

#### §30.1 「应用到桌面」崩溃：已加取证，但本次未复现

- 本次落地的修复（不是"只兜住"）：新增 `src\DesktopAgent.WinUI\ProductCrashDiagnostics.cs`——
  `internal static class ProductCrashDiagnostics`，`LogPath = Path.Combine(Path.GetTempPath(), "tessel-product-crash.log")`，
  `Install(Microsoft.UI.Xaml.Application)` 挂 `Application.UnhandledException`、`AppDomain.CurrentDomain.UnhandledException`、
  `TaskScheduler.UnobservedTaskException`，把 ISO 时间 + 来源 + 是否已处理 + `exception.ToString()` 追加写文件，整体包在
  try/catch 里（取证本身绝不允许变成新的故障源）；`App.xaml.cs` 构造里 `InitializeComponent()` 之后调用 `Install(this)`；
  `LayoutManagementView.DirectApply.cs` 的 `ApplyToDesktop_Click` 变成薄外壳（try/catch → 记日志 + 
  「应用流程发生未预期异常 · 已暂停继续应用」+ `ShowError`），原实现搬进 `ApplyToDesktopCoreAsync()`。
- 重建 x64 后**同样的操作序列没有再崩**：准备 → 设计 → 选 `6f0daaa5-4b3f-44a0-9021-fea1acc67cde` → 使用 → 执行 → 
  ↻ 预览（`MoveCountText=15`、`StayCountText=29`、`TargetDisplayText=\\.\DISPLAY1`、`可以应用`）→ 点「应用到桌面」→ 
  弹出确认对话框（`Button|id=PrimaryButton|name=应用到桌面` / `Button|id=CloseButton|name=取消`）→ 点 PrimaryButton → 
  **应用成功**：进程存活（PID 38192），`LayoutCanvasStatusText=应用流程已结束 · 请重新预览当前桌面`，
  `%TEMP%\tessel-product-crash.log` **未生成**，产品库 recovery session 15 → 16。
- 诚实边界：`0xc000027b`（Microsoft.UI.Xaml.dll 3.2.3.0，stowed exception）没有托管栈，本次也未复现，
  **根因未证实**。可能是首次对话框创建路径上的一次性状态。取证钩子已就位：再出现会留下托管栈。

#### §30.2 监督应用的结果（真机）

- 应用后重新预览同一模板：`MoveCountText=0`、`StayCountText=44`、
  `ApplyReadinessHeadlineText=不需要应用`、`ApplyReadinessDetailText=按当前预览，桌面已经是目标样子（要移动 0 项）。`
  → DISPLAY1 的 44 个图标已经在模板分区里。
- 产品库 `C:\Users\29719\AppData\Local\Tessel.Dev\desktop-agent.db` 新增 1 条 recovery session（15 → 16）；
  演练库不受影响（仍 15 条）。

#### §30.3 无人值守：候选变成"17 项"，仍然停在 preflight 风险预算

- 重新武装启动：`G:\work\LAB\drill-unattended\launch-armed-auto10.cmd`（`DESKTOPAGENT_DATA_DIR=G:\work\LAB\drill-unattended`、
  `..._REQUESTED_MODE=Automatic`、`MAX_MOVES=10`、`MAX_STEPS=20`），PID 13392；绑定模板 `6f0daaa5`（Revision 1）；
  启动后台监听（watchers 2；基线 146 个图标）。武装对话框静态文本含
  `Mode=ValidatedOpen; MaxMoves=10; MaxSteps=20; AllowStaging=False; FailureMode=None; RequestedMode=Automatic`。
- 14:14:00 在桌面新建 `dsh-drift-2.txt` → 14:14:02 文件系统 hint（`Unstable`：该文件只出现在第二次观察）→
  14:14:15 周期 `ChangesDetected`：
  `Added StableId=c:\users\29719\desktop\dsh-drift-2.txt Pos=(138,982) Monitor=\\.\DISPLAY1`（图标 146 → 147）→
  建议 `7d5a6be0-6654-4752-ad65-0c9eea468250`（模板 `6f0daaa5`、目标 `\\.\DISPLAY1`、**Moves=17 / ExecutionSteps=17**）→
  点火生效，日志给出裁决：
  `[B05] automatic cycle PreflightStopped | mode=Automatic/Automatic gate=DesktopAutomationUnattendedGate { IsOpen = True, Reason = None } paused=false cooldown=false fuse=false | suggestion=7d5a6be0-… lifecycleRev=1 | preflight=RiskBudgetBlocked riskMoves=17/10 riskSteps=17/20 staging=false withinBudget=false blockReasons=MoveCountLimitExceeded`
- 结论：只增加 1 个图标，建议仍要 **17 次移动**。模板规划会把 DISPLAY1 上的全部图标按分区**重排**，
  不只是"把新图标塞进分区"。所以在这块 44 图标的真实桌面上，"候选 ≤ 10"不是制造一个小漂移能达到的。
- 不采纳：抬高验收 harness 的 `MaximumAcceptanceMoveLimit=10`。那是刻意的验收护栏（§27.4），放宽它就是改靶子。

#### §30.4 两套规划面给出的移动数不一致（未解，记为待查）

- 同一模板 `6f0daaa5` + 同一块真机桌面：产品面板 fresh 预览「要移动 **0** 项 / 不需要应用」，
  PoC 自动执行侧的建议面给出 **Moves=17**。两个独立实现，一个说"已经一致"，一个说"要动 17 项"。
- 可能来源（未定位）：分区内排序是否计入移动、分区容量/溢出（`max_item_count`）处理不同、
  或产品预览只对"用户可见的目标分区"负责而建议面覆盖全屏图标。
- 影响：如果"面板说不需要应用、引擎说要做 17 次移动"长期并存，用户可见承诺与自动执行预算判定会长期打架。

#### §30.5 当前状态（可复现）

- 进程：只跑产品壳 `DesktopAgent.WinUI` PID 38192（14:07:49 启动，构建含崩溃取证）；
  武装 PoC `DesktopAgent.Diagnostics` PID 13392（14:11:49 启动，绑定 `6f0daaa5`，后台监听运行中）。
- 库：演练库 15 条 recovery session / 3 条站定授权（`state=0`）/ DISPLAY1×3 + DISPLAY2/3×1 实测证据；
  产品库 16 条 recovery session。
- 桌面新增文件（演练用，收尾删除）：`dsh-unattended-drill.txt`、`dsh-drift-1.txt`、`dsh-drift-2.txt`。
- 尚未发生的事：**一次真实的无人值守写入**。到这一步，链路里"证据、授权、门、裁决"都有真机证据，
  缺的是"候选小于预算"的一个真实场景。

## §31 「面板预览 0 项 vs 引擎建议 17 项」追查结论

### §31.1 事实

- 监督应用（§30.2）刚结束、桌面与模板一致时，产品面板「↻ 预览」给出
  `ApplyReadinessDetailText = 这次预览要移动 0 项。`，且应用按钮变成「不需要应用」。
- 在同一桌面上注入一个漂移文件 `dsh-drift-2.txt`（真机位置 `(138,982)`、`\\.\DISPLAY1`）后，
  引擎生成的建议是 `Moves=17 / Steps=17`，并落进诊断日志：
  `[B05] automatic cycle PreflightStopped | … preflight=RiskBudgetBlocked riskMoves=17/10
  riskSteps=17/20 staging=false withinBudget=false blockReasons=MoveCountLimitExceeded`。

### §31.2 两条路径不是两套实现

- 面板预览：`DesktopLayoutImpactPreviewService.Create` → `DesktopLayoutRuleAwarePreviewPlanner.Plan(...)`
  （`src/DesktopAgent.Application/DesktopLayouts/Templates/Editing/DesktopLayoutImpactPreviewService.cs:124`）。
- 引擎建议：`DesktopLayoutSuggestionBuilder` → **同一个** `Plan(...)`
  （`src/DesktopAgent.Application/DesktopSuggestions/DesktopLayoutSuggestionBuilder.cs:173`）。
- 引擎的 `Moves` 就是该预览计划的 `MoveRequiredCount`
  （`src/DesktopAgent.Core/DesktopLayouts/Templates/Execution/DesktopLayoutRuleAwareExecutionCandidate.cs:81`）。
- 跨屏入站差异已排除：`explicitInboundStableIds` 为 `null` 与空集等价
  （`src/DesktopAgent.Core/DesktopLayouts/Templates/Rules/DesktopLayoutIconRuleResolver.cs:30-32`）；
  面板传 draft 冻结授权（`…DesktopLayoutImpactPreviewService.cs:98`），引擎传 `null`
  （`…DesktopLayoutSuggestionBuilder.cs:173-179`），两边最终都是空入站集。

### §31.3 真因：槽位分配对「图标枚举顺序」敏感

- `src/DesktopAgent.Core/DesktopLayouts/Templates/Rules/DesktopLayoutRuleAwarePlacementPlanner.cs:120-176`：
  显式 `ZoneOverride` 命中先按 `Icon.Ordinal` 依次占用 `available[0..]`，随后「默认受管」图标
  同样按 `Icon.Ordinal` 顺序继续占用 `available[defStartSlot + consumed]`；
  `available` 是按 `ZonePlacementFlow` 展开的目标槽位（同文件 `:194-278`）。
- `AlreadyAtTarget` 的判定要求「当前坐标 == 目标槽位坐标，且 `MonitorDeviceName` 相同」
  （`src/DesktopAgent.Core/DesktopLayouts/Templates/Preview/DesktopLayoutRuleAwarePreviewPlanner.cs:551-560`）。
- 于是：**任何在枚举顺序上排在受管图标之前的图标，都会把它们整体挤后一格** ——
  本来一步不动的桌面，会因为"多了一个排序靠前的图标"变成"几乎全部要动"。

### §31.4 可复现证据（新增契约测试）

`tests/DesktopAgent.Tests/DesktopLayouts/Templates/Preview/DesktopLayoutRuleAwarePreviewPlannerOrderSensitivityTests.cs`
（3 条用例，34 ms 通过）：

| 用例 | 输入 | 结果 |
| --- | --- | --- |
| `FiveIconsSittingExactlyOnTheirCells_RequireNoMove` | 5 个图标正好落在 0–4 号槽位 | `AlreadyAtTargetCount=5`、`MoveRequiredCount=0` |
| `NewIconInsertedFirstInEnumerationOrder_ShiftsEveryManagedIcon` | 同一批位置 + 新图标 `Ordinal=0` | `AlreadyAtTargetCount=0`、`MoveRequiredCount=6` |
| `NewIconAppendedLastInEnumerationOrder_RequiresNoMove` | 同一批位置 + 同一新图标 `Ordinal` 最大 | `AlreadyAtTargetCount=6`、`MoveRequiredCount=0` |

### §31.5 结论与边界

- 「0 vs 17」**不是计数缺陷**：两次测量对应两个不同输入（有无漂移），且那 17 项都是真实需要移动的图标。
- 但它是**产品级弱点**：模板执行是「确定性全量重排」，而无人值守的风险预算是
  `MaxMoves=10`；只要桌面上多出一个排序靠前的图标，无人值守就再也过不了 preflight。
  真机上这正是 §30 观察到的现象。
- 可选修法（**未实施**，属 Core 语义变更，需要先定调）：在槽位分配里加一条「稳定性优先」规则 ——
  已经落在目标区内的图标，若其当前坐标仍是该区的可用槽位且尚未被占用，就保留原槽位。
  这样 1 个漂移的代价从 N 降到 ~1；代价是排布不再纯粹由模板顺序决定。
- 本结论只用 planner 的纯 Core 路径得出（无 GUI、无写盘、无真机写入）。
