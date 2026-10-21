# M3-B06 PoC Automatic Trigger Integration

## 目的

B05 已经完成“一次 automatic attempt”的全部内部链路。B06 把它接到 PoC 常驻运行面：

```text
SuggestionProcessed / 用户切换 Automatic
→ trigger pump
→ current lifecycle/runtime key
→ B05 RunCurrentAsync
→ 05C/B01/B02/B03/B04
```

Hint / FileSystemWatcher / periodic tick 永远不直接调用 Writer。它们最多通过 monitoring 产生 Suggestion lifecycle 变化，然后由 trigger 请求一次 current cycle。

## 普通 PoC 默认仍禁止 unattended Writer

新增 `M3AutomationPoCDefaults`：

```text
UnattendedGate = M3AProvisionalClosed
Cooldown = 30s
Fuse threshold = 3
MaxMoveCount = 5
MaxExecutionStepCount = 10
AllowTemporaryStaging = false
```

其中 staging=false 是普通 PoC 的保守默认风险预算，不代表 M3-B 能力不支持 staging。B03 已经能够复用 M2 staging/cycle；未来用户显式配置允许后可放开。

普通 composition root 只通过 `M3AutomationPoCDefaults.CreateRuntimePolicy()` 创建 Runtime Policy。

因此用户在 tray 选择 Automatic 时：

```text
RequestedMode = Automatic
EffectiveMode = Suggestion
BlockReason = UnattendedWriteGateClosed
GateReason = M2RealDualMonitorAcceptancePending
```

不会进入 B01/B02/B03 Writer 链。

未来真实 M3-B acceptance 必须使用独立、显式的 acceptance composition，不允许临时修改普通 PoC 默认 gate。

## Trigger pump

`M3AutomaticExecutionTriggerCoordinator` 订阅 `M3SuggestionMonitoringCoordinator.SuggestionProcessed`。

它只把事件解释为：

> 当前状态可能值得重新尝试一次 B05 cycle。

### TriggerKey

为了避免每个 periodic NoChanges 都重复 fresh reread，使用：

- SuggestionId
- LifecycleRevision
- RuntimeRevision
- EffectiveMode
- IsPaused
- IsCooldownActive
- IsFuseTripped

相同 key 最多尝试一次。

这同时允许没有 Revision 变化的时间状态自然重新触发：

```text
Cooldown=true, RuntimeRevision=12
→ 尝试一次，记录 key
→ 时间经过 30s
→ Cooldown=false, RuntimeRevision 仍=12
→ key 变化
→ 下一次 SuggestionProcessed 可再评估
```

Timed Pause 同理。

## Coalescing

Cycle 运行期间如果收到更新状态：

- 不排所有事件；
- 只保留最新 pending key；
- 当前 cycle 完成后再评估最新 current key。

这样 S1 cycle 期间 S2 到来不会丢，但 20 个重复 S2 event 也不会排 20 次。

## Pump lifetime / exit

`RequestCurrent()` 在同一 lock 下同时：

- 设置 pumpRunning；
- 创建 `_pumpTask = Task.Run(PumpAsync)`。

避免出现 `running=true` 但 StopAsync 还看不到可等待 tail 的半状态。

Tray Exit 顺序：

```text
trigger.StopAsync()
→ 停止接新 trigger
→ 等当前 B05 / M2 transaction 收口
→ stop monitoring
→ MainWindow 原 M2 Closing 安全检查
→ Application.Shutdown
```

如果 MainWindow 安全门取消退出，trigger 会重新 Start。

## Tray mode controls

04D tray 新增：

- 自动化状态：Requested / Effective / blocker；
- Manual；
- Suggestion；
- Automatic；
- 暂停自动整理 / 恢复自动整理；
- 重置自动整理熔断。

“暂停后台监听”和“暂停自动整理”明确分离：

- monitoring pause：眼睛停止观察；
- automation pause：Observation/Suggestion 可继续，automatic hand 不动。

Fuse reset 必须由用户显式确认，不自动恢复。

## Automatic result surface

Trigger cycle 完成后 tray 刷新 runtime 状态：

- FaultedAndPaused → balloon warning；
- FailureRecorded → 显示 Coordinator 状态，并提示 cooldown/fuse；
- Success → 状态栏自然显示 cooldown。

B06 不把 tray balloon 当 Windows App Notification 替代品；它只做 automatic runtime 本地状态反馈。

## 自动测试

覆盖：

- 无 Active Suggestion 不跑 cycle；
- 同一 lifecycle/runtime key 重复请求只执行一次；
- 新 lifecycle revision 可再次执行；
- cooldown 自然到期即使 Runtime Revision 不变也形成新 key；
- cycle 运行期间新 Suggestion 被 coalesce 并在随后执行；
- StopAsync drain 当前 cycle 并丢弃 pending；
- Tray / trigger 无直接 Writer 依赖；
- ordinary PoC gate 固定 provisional closed；
- ordinary PoC risk/runtime 默认参数冻结。

## 下一步

M3-B07 Controlled Automatic Acceptance Composition：为真实 Windows 实机验收增加一个独立、显式、Debug-only 的 `ValidatedOpen` 启动入口。

它必须与现有 M2 Writer fault-injection 模式互斥，并在启动时大字警告：本次进程允许 unattended real write。

在用户实际回到 Windows 主力机执行验收前，普通 DesktopAgent 启动路径继续保持 gate closed。
