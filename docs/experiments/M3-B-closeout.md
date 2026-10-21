# M3-B Closeout

## 结论

M3-B 的 automatic execution safety chain 已完成代码、CI 与 Windows 实机验收闭环。

本阶段完成的是：在保持现有 M2 Coordinator / Recovery / Writer 语义不被绕过的前提下，把 M3 的 Suggestion、Runtime、fresh reread、fresh Candidate、安全门、真实 Coordinator、cooldown/fuse 与 tray trigger 串成一条可验证的 automatic execution chain。

M3-B **不等于**“普通 PoC / Release 默认开放 unattended write”。普通 composition root 仍保持 provisional-closed gate；B07 的 `ValidatedOpen` 只存在于显式、Debug-only、受控实机验收入口。是否在未来产品 composition 中默认开放自动写入，是后续产品策略决定，不由本 closeout 偷偷改变。

## 最终执行链

```text
Observation / Diff
→ Suggestion lifecycle
→ Requested / Effective Runtime Policy
→ Candidate Risk Budget
→ 05C Preflight
→ B01 fresh stable Observation
→ fresh template / rules load
→ fresh M2 Candidate rebuild
→ B02 automatic safety gate
   → exact lifecycle/runtime revision
   → unresolved recovery == 0
   → fresh candidate invariants
   → fresh risk budget
→ B03 existing M2 Coordinator bridge
   → fresh topology / icons / capabilities
   → latest ExecutionPlan
   → pre-writer scope gate
   → safety snapshot
   → recovery checkpoint
   → existing Writer
   → verification / rollback
→ B04 execution outcome feedback
→ cooldown / consecutive failure fuse
→ monitoring re-observes real desktop
→ lifecycle naturally retires / rebuilds
```

关键原则没有改变：

1. Store 保存意图、历史和证据，不冒充当前桌面事实。
2. Windows Shell / fresh Observation 才是执行时现实。
3. 旧 Suggestion Candidate 永远不能直接成为 automatic execution input。
4. automatic attempt 必须重新读取当前事实并重新生成 fresh Candidate。
5. M3 不新增第二套 Writer，不绕过现有 M2 Coordinator / Recovery。
6. hint / FileSystemWatcher / periodic tick 只触发观察与建议处理，不直接触发 Writer。
7. 任一安全阶段不 Ready，后续阶段不跨级补票，fail closed。
8. 已进入 M2 transaction 后按现有 snapshot / checkpoint / verification / rollback 语义完整收口。

## 阶段完成项

### Runtime / risk / preflight

- M3-05A：Requested / Effective mode、Pause、Cooldown、Fuse。
- M3-05B：Candidate move / step / staging 风险预算。
- M3-05C：current lifecycle + runtime + risk 的只读 preflight。

### Fresh boundary 与 automatic safety

- B01：旧 Suggestion 只作为意图凭证；执行前 fresh reread + fresh rebuild。
- B02：runtime/lifecycle revision、recovery、fresh candidate invariants 与 fresh risk 再检查。
- B03：复用现有 `TargetDisplayLayoutExecutionCoordinator`，在 Writer 前最后一厘米做 scope gate。
- B04：只有真实 Coordinator 返回 execution result 时才记录 success / failure。
- B05：固定串联 05C → B01 → B02 → B04(B03)，non-queued single-flight。
- B06：接入 tray / SuggestionProcessed trigger pump，重复 key 去重，运行中只保留最新 pending key。

### Controlled acceptance

- B07：Debug-only `ValidatedOpen` 三重武装入口：完整 env → startup Yes → tray 手动 Automatic。
- #83：CI / teardown hang guard，不改变生产 transaction drain 语义。
- #84：修复 WPF `Window.Close()` 后 tray 再 `Show()` 的生命周期缺陷。
- #85：Debug acceptance-only pre-writer safe failure injection，用于验证 cooldown / fuse。

## B07 Windows 实机验收

### A. single-screen direct move

PASS。

- `Moves=1 / Steps=1 / Staging=False`
- 用户从 tray 手动切换 Automatic。
- `M2-B.txt` 真实只移动一次到目标位置。
- 其他图标无额外移动。
- monitoring 随后重新观察真实桌面，旧 Suggestion 自然退休。
- 未观察到 duplicate automatic write。

### B. stale facts / stale Suggestion

PASS。

- 先生成 Active Suggestion。
- 停止 monitoring 保留旧票。
- 人工把桌面恢复到已经满足模板的状态。
- 再请求 Automatic。
- B01 fresh reread / rebuild 得到当前事实无需动作，Writer-visible movement = 0。

### C. cooldown / duplicate suppression

PASS。

- 成功 automatic write 后 runtime 进入 cooldown。
- cooldown 期间 Effective 降为 Suggestion。
- monitoring 重新观察真实桌面并退休旧 Suggestion。
- cooldown 到期后 Effective Automatic 恢复，但没有新的 Active Suggestion，因此没有重复写入。

### D. controlled failure / fuse

PASS。

Debug acceptance-only failure seam 在 safety snapshot creation 故意失败，位置晚于 fresh/scope gates、早于 recovery checkpoint 与第一笔 Writer。

真实链路：

```text
FailedBeforeWrite
→ RecordExecutionFailure
→ cooldown
→ 同一仍 Active Suggestion 再尝试
→ 第 3 次 consecutive failure
→ Fuse
```

结果：

- 三次均 `FailedBeforeWrite`。
- 测试图标全程 Writer-visible movement = 0。
- 第三次连续失败触发 Fuse。
- Fuse 后额外等待仍无第 4 次 attempt。

### E. dual-display topology stale

PASS。

UU 远程虚拟副屏作为 Windows 真实扩展显示器参与 topology。为 E 建立本地 acceptance fixture `M2 Acceptance E`：单 Zone B、`MaxItemCount=1`、仅 `M2-B.txt` 使用 `ZoneOverride -> B`。

双屏时 Preview 确认：

```text
IsComplete=True
IsExecutable=True
MoveRequired=1
ExecutionSteps=1
UsesTemporaryStaging=False
```

随后：

```text
双屏下生成 Active Suggestion，Target=副屏
→ 停止 monitoring，保留旧票
→ 断开副屏，当前 topology 只剩主屏
→ 旧 Suggestion 仍指向已消失的 target
→ tray 手动请求 Automatic
→ B01 fresh reread / fresh rebuild fail closed
→ Coordinator / Writer 不进入
→ Writer-visible movement = 0
```

该场景不会进入 `FailureRecorded -> Cooldown`，因为它在 B01 fresh handoff 阶段已经正常安全停止；这不是 execution failure。

## CI 终局

栈顶 #85 head `6ec8758a9401829f1313d885dbe710afd3af0310`：

- GitHub Actions #259 全绿。
- Debug Build ✅
- Release Build ✅
- Release tests + blame-hang ✅
- 1742 / 1742 passed，0 failed，0 skipped。
- diagnostics artifact upload ✅

#83 对已识别的 trigger-test teardown hang 放大器增加 guaranteed release/finally，并保留 15min job timeout、2min blame-hang 与 diagnostics artifact 作为后续黑匣子。

## M3-B Done 定义

以下条件全部成立，因此本阶段记为 Done：

- [x] old Suggestion 不可直接执行，必须 fresh reread / fresh rebuild。
- [x] automatic risk 由 fresh Candidate 重新投影，而不是复用旧票风险。
- [x] unresolved recovery / runtime / lifecycle stale 均 fail closed。
- [x] Writer 前有 current-plan scope gate。
- [x] 只复用现有 M2 Coordinator / Writer / Recovery 链。
- [x] success 有 cooldown，避免重复快速执行。
- [x] failure 有 cooldown + consecutive failure fuse。
- [x] trigger single-flight / dedupe / coalescing 已覆盖。
- [x] Windows real direct Writer 已验证。
- [x] stale facts Writer=0 已验证。
- [x] cooldown / duplicate suppression 已验证。
- [x] safe failure → fuse 且 Writer=0 已验证。
- [x] dual-display topology stale → pre-writer fail closed 已验证。
- [x] tray close/reopen/exit 生命周期回归通过。
- [x] CI hang guard 与 diagnostics 已建立。

## 非阻断遗留

这些内容不阻止进入 M4，不在 M3-B closeout 中扩大范围：

1. **Last Automatic Cycle 只读诊断**
   - 当前 tray 只主动提示 `FaultedAndPaused` / `FailureRecorded`。
   - `FreshHandoffStopped / FreshCandidateRejected / NoActionNeeded` 这类正常安全停止缺少明确的最后一轮结果展示。
   - 后续可增加 `LastCycle / Stage / StopReason / CoordinatorReached` 只读 surface，提升排障体验，但不改变安全语义。

2. **Windows App SDK unpackaged runtime / packaging**
   - `dotnet run` 下可能因为 package identity / Singleton runtime 不满足而退化为 diagnostics-only notification。
   - 不影响 Suggestion / automatic safety chain；正式安装器与 runtime deployment 后续独立处理。

3. **历史 xUnit analyzer warnings**
   - 当前不影响 1742 项测试通过。
   - 与本阶段已解决的 testhost teardown hang 不是同一问题。

4. **虚拟副屏 DeviceName 跨重连可能变化**
   - B07-E 实测 UU 虚拟显示器曾以不同 `DISPLAYxx` 名称重新枚举。
   - 因此后续若需要跨 session 持久化“某块物理显示器”的身份，不应把瞬时 DeviceName 单独当作长期硬身份。
   - 当前 SelectedTarget acceptance 每次从 fresh topology 显式选择，不受此观察影响。

## M4 起点约束

M4 可以开始，但必须把 M3-B 当成既有安全执行底座，而不是重写执行链。

M4 的新增智能、策略、规划或长期行为必须遵守：

```text
M4 intent / policy / planning
→ M3 Observation / Suggestion / Runtime
→ existing M3-B fresh + safety chain
→ existing M2 Coordinator / Recovery / Writer
```

禁止：

- M4 直接持有或调用 `IDesktopIconPositionWriter`；
- M4 缓存长期 `Authorized=true`；
- M4 把历史 Store 当作当前桌面事实；
- M4 绕过 fresh Observation / fresh Candidate；
- M4 因“模型确信”而跳过 recovery / topology / scope gate；
- M4 把虚拟显示器瞬时 `DISPLAYxx` 名称当成跨 session 永久身份。

## PR 栈说明

本 closeout 仍保持 stacked Draft 工作流，不在此处合并父 PR。

M3-B 主链位于 #73–#85，其中 #71–#72 提供 Windows notification / tray 常驻基础。#84 是实机过程中发现并修复的独立 tray/window lifecycle 缺陷，#85 提供受控 failure/fuse 验收 seam。

后续是否 squash / rebase / 分段 merge，由单独的 integration 收口决定；本文件只冻结“当前行为与验收结论”，不通过改 base/merge 来改变历史。