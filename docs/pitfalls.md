# 主要阻断点与解决过程

这份文档记录开发过程中真正卡住过我们的问题：现象、怎么定位、根因、怎么解、以及可以迁移到
别处的教训。**不是变更日志**，失败和误判都保留着。

按领域分组，每条都经过真机验证（Windows 11 三屏环境）。

---

## A. Shell 与坐标系

### A1. 图标「全贴着屏幕最左边竖排」，而产品概览显示正常

**现象**：用户在真实桌面上看，三块屏的图标都挤在各自最左侧竖着一列；但产品「我的桌面」里的
分区图看起来是对的。

**定位**：直接调 `IFolderView.GetItemPosition` 拿到的坐标，和 `GetDC` 截图上图标实际像素位置
差一个固定偏移；而这个偏移量恰好等于「虚拟桌面左上角」到「主屏左上角」的距离。

**根因**：Explorer 的 `IFolderView` / `SysListView32` 用**视图客户区坐标**，客户区原点在
虚拟桌面的左上角；产品其余部分（显示器 Bounds、Overlay 窗口、鼠标事件）用**屏幕坐标**。
主屏位于虚拟桌面 (0,0) 时两者一致，所以这个问题在单屏和"主屏在左上角"的双屏上看不出来。
这台机器的副屏是 `(-1080,-210)`，主屏不在原点 —— 于是每个图标都少了一份偏移。

**解法**：新增 `src/DesktopAgent.WindowsInterop/DesktopIcons/DesktopViewCoordinateSpace.cs`，
把换算收敛成唯一入口：

```csharp
DesktopViewCoordinateSpace.ClientOrigin   // Progman 客户区原点（屏幕坐标）
.ToScreen(viewPoint)                       // 读路径
.ToView(screenPoint)                       // 写路径
```

- 读：`GetItemPosition` → `ToScreen`；
- 写：屏幕坐标 → `ToView` → `SelectAndPositionItems`；
- 写完回读，再 `ToScreen` 与期望比对。

**教训**：跨坐标系的代码只能有**一个**换算点；出现第二个，就一定有一边是错的。而这个问题在
"物理位置恰好为 0"的默认环境里是隐形的 —— 只有多屏且主屏不在左上角才会暴露。

### A2. 网格实测值「看起来对」，但它其实是推断出来的

**现象**：早期版本在某些显示器上拿到了网格间距，看起来合理，也能算出目标坐标，但落到真桌面
会错一格。

**根因**：证据来源（实测 / 推断）在内部被当作数值传递，`Inferred = 1`，映射到执行层时
`Measured = 1` —— 一次数值转换就把推断升级成了实测，属于典型的 fail-open。

**解法**：

- 引入显式枚举 `ExplorerGridEvidenceSource { Measured, Inferred }`；
- 证据映射改成**显式 switch**，禁止数值转换；
- `Measured` 的门槛写死：每轴至少 3 个主动样本且完全一致；
- 授权入口只认 `CanAuthorizeExecution(fingerprint)`：来源必须是 `Measured`，且指纹与当前一致。

**教训**：安全相关的等级不要用整数表示。枚举 + 穷举 switch 编译器会逼你把新分支想清楚。

### A3. 换了分辨率，旧的实测事实还在用

**现象**：改完缩放比例后，预览还能算，但应用出来的位置整体偏移。

**根因**：事实记录里只有"网格是多少"，没有"在什么环境下测的"。环境变了，事实看起来仍然有效。

**解法**：`ExplorerGridTopologyIdentity` 把环境压成哈希并随事实一起存：

```text
explorer-topology-v1:SHA256(auto=<自动排列>|每屏 DeviceName|Bounds|WorkArea|DpiX|DpiY|IsPrimary)
```

指纹不匹配 → 事实视为不存在 → 回到校准引导。故意**不**把图标相位放进指纹，那是派生值。

**教训**：环境绑定的结论必须显式绑定环境，并且失效要自动、可见，而不是靠人记得"我改过缩放"。

---

## B. 撤销与恢复

### B1. 撤销写下的会话被标成了「整理」，导致可以无限撤销撤销

**现象**：撤销完成后，再点撤销，产品又给出一份"撤销"，而且它撤销的是上一次撤销。

**定位**：`desktop_layout_recovery_sessions.origin` 列在 schema v13 才加；加完之后实测发现
撤销产生的行 `origin` 仍然是 `organize`。

**根因**：`Origin` 字段在三个**重建记录**的地方没有拷贝：

- `TargetDisplayLayoutExecutionCoordinator.cs:1599` 与 `:1655`（`new RecoverySessionRecord` 没带 Origin）；
- `RecoveryOrchestrator.cs:107`（同上）。

数据库 UPDATE 不碰 origin（只在 INSERT 写），所以问题完全出在内存里那三次重建。

**解法**：三处补 `Origin = …`；新增守卫测试
`RecoverySessionPersistenceWiringTests.CheckpointRestoreOriginReachesTheStoredRow`，用真实持久化
链路跑一遍，断言落库的 origin 就是传进去的 origin。

**教训**：加字段时最容易漏的不是"读写 SQL"，而是**把对象复制一遍再存**的中间层。这类代码
没有编译错误，也不会在第一轮测试里暴露。

### B2. 跨屏撤销被自己的守卫拦住：快照的目标屏是 null

**现象**：把三块屏的图标汇聚到一块屏后想撤销，直接报
「快照的 `TargetMonitorDeviceName` 为 null，不能用于指定显示器恢复」。

**根因**：还原规划器要求"快照的目标屏 == 本次还原目标屏"，这对**单屏小范围还原**是正确的守卫；
但汇聚类写入的检查点覆盖整个桌面，`TargetMonitorDeviceName` 本来就是 null（它不属于某一块屏）。

**解法**：给该守卫加显式开关 `allowSnapshotFromAnotherDisplay`，只有撤销路径传 true；同时让
受影响的屏从快照里**逐项算出来**（`DesktopLayoutRollbackRestoreTargetResolver`），按屏循环，
每屏重新观察。

**教训**：`fail-closed` 是对的，但守卫要建模"适用范围"。同一个校验在不同场景下可能需要
不同的严格程度 —— 关键是显式写出来，而不是取消守卫。

### B3. 「回收站不是文件系统项目」挡住整块还原

**现象**：跨屏还原进行到第三块屏时失败，理由：`当前图标 "回收站" 不是文件系统项目。`

**根因**：可移动性校验（是不是文件系统项、能不能移动）排在"是否已经到位"之前。回收站是系统项，
永远不可移动 —— 但它在这次还原里本来就不需要动（目标位置就是原位）。

**解法**：把 `AlreadyAtTarget`（位置与显示器都符合期望）提到可移动性校验之前。新增三条测试：

- `NonFileSystemItem_AlreadyAtTarget_IsNotBlocked`
- `SystemItem_AlreadyAtTarget_IsNotBlocked`
- `NonFileSystemItem_NeedsMove_StillBlocked`（不能顺带放宽真正要动的系统项）

**教训**：判定顺序本身就是语义。先问"要不要动"，再问"能不能动"；反过来会把一堆无需处理的
项目误判成错误。

### B4. 撤销确认框里说的目标屏，和实际落点不是同一块

**现象**：确认文案说「会把 45 个项目放回 `\\.\DISPLAY2`」，实际落到 `\\.\DISPLAY3`。

**根因**：文案取的是会话记录里的 target 屏；而跨屏还原时，落点应该从检查点里**逐项**算。

**解法**：`DesktopLayoutRollbackPreflight.DescribeApprove` 改为从检查点逐项统计落点屏：单屏写屏名，
多屏写「涉及 N 块屏：…」。配套测试锁住两种形态。

**教训**：给用户看的结论必须和真正执行用的是**同一份数据**。借道别的字段拼文案，等价于有两套真相。

---

## C. 设计、预览与界面

### C1. 面板预览说「0 项要移动」，引擎建议却说「17 项」

**现象**：同一个布局模板，面板预览算出 0 项移动，后台建议引擎算出 17 项。两个数字差得离谱。

**定位**：两条路径最终都调用同一个 `DesktopLayoutRuleAwarePreviewPlanner.Plan`
（面板走 `DesktopLayoutImpactPreviewService`，引擎走 `DesktopLayoutSuggestionBuilder`），
输入参数也一致，所以不是"两套算法"。

逐项打印后看到：`AlreadyAtTarget` 用的是**像素精确相等**，而槽位分配先按 `Icon.Ordinal`
给规则覆盖项排位、再按同一顺序给默认受管项排位。于是桌面上新增一个排序靠前的图标，会把
后面所有受管图标的槽位整体挤后一格 —— 0 变成 17。

**根因**：算法对**枚举顺序**敏感，而两条路径传入的图标顺序来自不同的观察快照。

**解法**：不改成"模糊相等"（那会掩盖真实的未到位状态），而是把行为锁进契约测试
`DesktopLayoutRuleAwarePreviewPlannerOrderSensitivityTests`：同一份数据按不同顺序输入，
移动数分别是 0 / 6 / 0，明确记录了"顺序会影响结果"。

**教训**：两条路径调用同一算法却结论不同，差异通常在**输入**（顺序、时点、来源），
不在算法。差得越离谱越要先打印输入。

### C2. 工作区「一次性精确预览凭据」失效，导致应用被反复拒绝

**现象**：生成精确预览 → 点应用 → 被拒「当前精确预览已不能安全应用」→ 回 AI 助手重新生成
→ 又被拒，循环。

**根因**：凭据是一次性的且绑定当时的桌面观察。应用前的 fresh check 发现桌面与预览不一致时
（fail-closed，一步没写），凭据**也已经不可用**，只能重新生成。如果桌面持续与期望不一致，
就成了死循环。

**解法（分两步）**：

1. 生成预览前先做一次 fresh capture，避免用过期观察去生成；
2. 增加语义复用门 + **字段级差异诊断**：拒绝时点名具体项目，并给出「预期 (x, y)，实际 (x, y)」；
3. 状态栏明确写「一次性凭据已失效 / 回到 AI 助手重新生成」。

**教训**：一次性授权机制必须回答两个问题，否则用户会困在循环里：**为什么失效**、**怎么恢复**。

### C3. 冻结预览把「桌面线框」开关一起锁死了

**现象**：生成精确预览后，右侧一整排控件变成只读，连看一眼线框都不行。

**根因**：判定写成了"只要存在冻结预览或逻辑预览，就整排禁用"。而产品本意是——**线框只读审阅
不应该消费应用凭据**。

**解法**：拆分禁用来源，逐控件决定；线框开关永远可用且与凭据无关。新增契约测试覆盖
「开关线框前后 `ApplyToDesktopButton` 状态不变」。

**教训**：状态门是分级的（可读 / 可改 / 可执行），一刀切会误伤只读操作。

### C4. 设置里改了分区框外观，已显示的线框没反应

**现象**：设置页点「明显」→ 应用，设置文件确实变了，但桌面上已经打开的线框一点变化都没有。

**根因**：`DesktopZoneOverlayHost.Show(...)` 在调用时把外观**快照**进窗口；`DefaultAppearance`
的 setter 只写静态字段，不会通知任何已创建的实例。

**解法**：加静态事件 `DefaultAppearanceChanged` + 每个实例记录最近一次 `Show` 的参数；收到事件时
在原地重建（用 `SameAppearance` 比较，值没变就不重建）。两条测试：只在真的变化时通知、
`Custom` 预设改厚度也要通知。

**教训**：静态默认值 ≠ 已实例化对象的响应。凡是有"默认值 + 已展示实例"的组件，都要显式做刷新路径。

---

## D. 自动化与授权

### D1. 验收钥匙能开门，但门后面没有引擎

**现象**：用 Debug 验收钥匙武装后，安全门确实打开了（`UnattendedGate = ValidatedOpen`，
`canAttempt = True`），但跑起来一步都没动。

**定位**：把产品壳整个目录 grep 一遍 `UnattendedGate|AutomaticSafetyGate|ValidatedOpen` —— 零命中。
引擎只在 `src/DesktopAgent.Poc` 里。而「记住这块屏」的**签发面**只在产品壳
（`LayoutManagementView.WorkspaceApply.cs` 的 `GrantDisplayAuthorizationsAsync`）。
两半分别住在两个进程、两个数据库目录里。

**解法**：让引擎能读带授权的库 —— 新增 `DESKTOPAGENT_DATA_DIR` 环境变量覆盖数据根目录
（`DesktopAgentDataPaths.ResolveRootDirectory`），把产品库复制一份给演练用，PoC 指过去。
真机验证：工作台状态栏打印的数据库路径变成演练库，快照写入确实落在演练库，旧库未动。

**教训**：当一条安全链横跨两个进程/两个库时，先验证**两端是否在同一根链上**，再谈能力。
不然会花很久去调一个根本不存在的连接点。

### D2. 点火成功，但被「风险预算」挡住，而且这是故意的

**现象**：补上自动触发（`RequestCurrentIfAutomatic`）后，日志终于出现了自动循环：

```text
[B05] automatic cycle PreflightStopped | gate=DesktopAutomationUnattendedGate { IsOpen = True }
      | preflight=RiskBudgetBlocked riskMoves=17/10 riskSteps=17/20 blockReasons=MoveCountLimitExceeded
```

门是开的、授权在、实测在，但候选要移动 17 项，验收护栏只允许 10 项。

**决策**：**不**抬高护栏。`MaximumAcceptanceMoveLimit = 10` 是刻意设定的验收条件，为了"跑出
一次真实写入"而放松它，等于把通过标准改成"能不能通过"。

**结论**：如实记录「真实无人值守写入未发生」，并把触发与可审计日志单独提交 —— 发动机证明了，
只是车没开出去。产品形态里无人值守默认仍然关闭。

**教训**：验收失败时最容易做的错事就是调门槛。宁可写"没做到"，也不要制造一次假通过。

### D3. 界面验收最初根本做不了：自动化和托盘都不通

**现象**：想在真机上验收界面，Computer Use 通道不可用；改用 UIA，产品窗口里的
`NavigationViewItem` 不支持 `Invoke`/`Expand`；想点托盘切模式，Win11 的托盘是 XAML island，
UIA 枚举 `Shell_TrayWnd` 后代得到 **0 个 Button**。

**解法**：

- 点击改走 Win32：`SetCursorPos` + `mouse_event`，坐标从 `GetWindowRect` + 窗口内偏移算；
- 截图改走 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`，能截到被遮挡的窗口，且不含壁纸
  （顺带解决了"截图里有用户桌面"的隐私问题）；
- 点击前必须先确认窗口在前台（比较 `GetForegroundWindow()`），否则点击会落到遮挡窗口上；
- 把这一套固化成 `tools/uia-acceptance/`（`Act-Ui.ps1` / `Win-Windows.ps1` / `Probe-Button.ps1`）。

**教训**：不要在不可自动化的界面上建验收路径。发现走不通时先换通道（Win32 消息 / 截图 +
坐标点击），不要硬刚 UIA。另外：**脚本文件必须是纯 ASCII** —— 中文在 `.ps1` 里按 ANSI 读入
会变乱码并直接编译失败。

---

## E. 工程与发布

### E1. Release workflow 里 `dotnet test --no-build` 找不到测试 dll

**现象**：本地一切正常，CI 的 release 工作流在 Test 步骤失败：

```text
The test source file "...\tests\DesktopAgent.Tests\bin\x64\Release\net10.0-windows10.0.19041.0\DesktopAgent.Tests.dll"
provided was not found.
```

**根因**：build 与 test 都传了 `-p:Platform=x64`，输出落在 `bin\x64\Release\...`；但
`dotnet test --no-build` 的查找路径用的不是同一个中间属性，于是去 `bin\Release\...` 找。

**解法**：build / test **不传** `Platform`（与 `build.yml` 保持一致），只有 publish 传。
另外 `WindowsAppSDKSelfContained=true` 也不能走命令行 `-p:`（会传播到类库，报
`should not be applied to a class library`），要写死在 WinUI 项目文件里。

**教训**：命令行属性会被全局传播，中间输出路径会跟着变。属性要按"谁真的需要"分配。

### E2. 用 Release 跑了一下，数据「不见了」

**现象**：开发时顺手用 Release 启动了一次，界面变成空的 —— 没有设计、没有校准记录。

**根因**：渠道身份由编译符号决定（`TesselProductIdentity.cs`）：

| 编译符号 | 渠道 | 显示名 | 数据目录 |
| --- | --- | --- | --- |
| `TESSEL_DEV_CHANNEL` | Dev | `Tessel Dev` | `%LOCALAPPDATA%\Tessel.Dev` |
| `TESSEL_PACKAGING_TEST_CHANNEL` | PackagingTest | `Tessel Packaging Test` | `%LOCALAPPDATA%\Tessel.PackagingTest` |
| 都没有 | Stable | `Tessel` | `%LOCALAPPDATA%\Tessel` |

Release 构建天然是 Stable，于是读的是另一个目录。

**解法**：开发固定 `-c Debug -p:Platform=x64`，并在开发文档里写死这条；发布包的 Stable 数据与
开发数据完全隔离，互不影响。

**教训**：编译期的身份分流必须写进文档，否则表现为"数据莫名其妙丢了"，而被怀疑的通常是持久化层。

### E3. 发布包 226 MB，其中 42 MB 是产品用不到的东西

**现象**：自包含发布目录 530 个文件 226 MB，zip 88 MB。

**定位**：按体积排序，`onnxruntime.dll`（20.7 MB）与 `DirectML.dll`（17.8 MB）排在最前 ——
这是 Windows App SDK 的 ML 组件，本产品完全没有用到。

**解法**：在 `tools/publish.ps1` 加裁剪步骤，删除 28 个文件约 42 MB：
`onnxruntime.dll`、`DirectML.dll`、`Microsoft.ML.OnnxRuntime.dll`、`Microsoft.Windows.AI.*`、
`Microsoft.Windows.Vision*` 等。结果：目录 184 MB、zip 70 MB、安装器 48 MB，启动验证正常。

**教训**：框架的"全家桶"按需删除比换框架便宜得多；但删完必须真机启动验证，不能只看体积。

---

## 可以迁移的经验（汇总）

1. **坐标系只能有一个换算点** —— 第二个一定错。
2. **安全等级用枚举，不用整数** —— 数值转换会静默 fail-open。
3. **环境绑定的结论要带指纹** —— 否则环境变了它还在"有效"。
4. **加字段先查"对象重建点"** —— 最容易漏的是复制构造，不是 SQL。
5. **守卫要建模适用范围** —— 同一校验在不同场景严格度不同，写显式开关而不是删守卫。
6. **给用户的结论要和执行共用同一份数据** —— 借道别的字段就会出现两套真相。
7. **两条路径结论不同，先查输入顺序与时点** —— 算法通常没错。
8. **一次性凭据必须回答"为什么失效 / 怎么恢复"** —— 否则用户困在循环里。
9. **状态门是分级的** —— 可读 / 可改 / 可执行不能一刀切。
10. **静态默认值不会通知已存在的实例** —— 有默认值的组件都要显式刷新路径。
11. **跨进程的安全链先验证"接上了没有"** —— 别调一个不存在的连接点。
12. **验收失败时不要调门槛** —— 宁可记"没做到"，不要制造假通过。
13. **不要在不可自动化的界面上建验收路径** —— 换通道，别硬刚。
14. **命令行属性会全局传播** —— 按"谁需要"分配。
15. **编译期身份分流要写进文档** —— 否则表现为数据丢失。
