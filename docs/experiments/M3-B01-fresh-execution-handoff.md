# M3-B01 Fresh Execution Handoff

## 目的

M3-B 开始接近真实执行链，但第一步仍然不调用 Writer。

B01 解决的核心问题是：**旧 Suggestion Candidate 永远不能直接成为自动执行输入。**

```text
05C ReadyForFreshSafetyCheck
→ verify current Active Suggestion + lifecycle revision
→ fresh stable Observation
→ fresh template / rules load
→ fresh M2 Preview / Candidate rebuild
→ verify lifecycle still unchanged
→ verify new Candidate is actually fresh
→ ReadyForAutomaticSafetyGate
→ STOP
```

`ReadyForAutomaticSafetyGate` 不是执行授权。

## 为什么不能直接执行 Suggestion.Candidate

Suggestion Candidate 只证明：在 `SourceObservationId` 对应的旧桌面事实下，M2 Planner 当时能生成一份合法 Candidate。

从 Suggestion 产生到真正准备自动整理之间，可能发生：

- 图标位置改变；
- 显示拓扑改变；
- 主显示器改变；
- Auto Arrange 改变；
- IconSpacing 改变；
- 模板/规则被编辑；
- Suggestion 被替换、Suspended、dismiss；
- 05C decision 自身变 stale。

因此 B01 只把旧 Suggestion 当作自动化意图凭证：Template binding + expected Suggestion identity。真实 Candidate 必须重建。

## Fresh Candidate Rebuilder

新增 `IDesktopLayoutFreshCandidateRebuilder` / `DesktopLayoutFreshCandidateRebuilder`。

输入：

- `DesktopLayoutSuggestionBinding`
- fresh `DesktopObservation`

内部重新：

1. 读取模板；
2. 检查 SelectedTarget binding；
3. 读取规则集；
4. 解析当前 target display；
5. 使用 fresh Observation icon order / spacing / capabilities；
6. 调 `DesktopLayoutRuleAwarePreviewPlanner`；
7. 调 `DesktopLayoutRuleAwareExecutionCandidatePlanner`。

不需要 State Diff，不依赖 Writer / Coordinator。

## Handoff stale gates

`DesktopAutomationFreshExecutionHandoff` 在三个位置检查旧 05C decision 是否仍然有效：

1. fresh capture 前；
2. fresh capture 后、rebuild 前；
3. rebuild 后。

不仅检查 SuggestionId，还要求 lifecycle Revision 完全一致。

因此即使同一 Suggestion 曾经 Suspended → Active，旧 preflight 也不能继续使用，必须重新做 05C。

## Fresh invariants

B01 额外拒绝：

- fresh ObservationId 与旧 Suggestion.SourceObservationId 相同；
- fresh rebuilder 返回与旧 `Suggestion.Candidate` 同一对象实例；
- fresh Candidate target device 与旧 Suggestion target 不同。

这些 invariant 主要用于防止未来代码“优化”时偷偷绕过 fresh boundary。

## 状态

- `PreflightNotReady`
- `SuggestionNoLongerCurrent`
- `ObservationUnstable`
- `ObservationUnavailable`
- `ObservationNotFresh`
- `FreshCandidateNoActionNeeded`
- `FreshCandidateRejected`
- `FreshCandidateNotFresh`
- `TargetChanged`
- `ReadyForAutomaticSafetyGate`

只有最后一种能进入 B02。

## 当前真实部署边界

当前 composition root 仍使用 `M3AProvisionalClosed`，所以真实运行不会自然产生可进入 B01 的 unattended ready decision。

M3-B 现在允许继续开发、自动测试与 fault-injection；在 M2 双屏和 M3-B 实机安全验收完成前，不默认开放真实 unattended Writer。

## 自动测试

覆盖：

- non-ready preflight 不 capture / rebuild；
- S1 在 capture 前被 S2 替换；
- lifecycle 在 capture 中变化；
- Unstable / Unavailable；
- 假 fresh source 返回旧 ObservationId；
- rebuilder 返回旧 Candidate 同一实例；
- truly fresh Candidate 最多到 Automatic Safety Gate；
- fresh rebuilder 同一 observation 连续重建得到不同 Candidate 实例；
- NoOp / Auto Arrange / missing template / incomplete binding / missing target fail closed；
- Handoff / Rebuilder 均无 Writer 依赖。

## 下一步

M3-B02：针对 B01 的 fresh Candidate 做 Automatic Safety Gate。

B02 应重新基于**当前 runtime + fresh Candidate 风险 + M2 execution evidence**判断是否可产生一次性 execution authorization。仍然不得缓存长期 `Authorized=true`。
