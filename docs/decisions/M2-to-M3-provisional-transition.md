# M2 → M3 临时并行推进决策

> 决策日期：2026-08-08  
> 状态：生效中  
> 适用范围：真实双显示器 topology acceptance 补完之前

## 1. 背景

M2 的核心实现、自动化测试和单显示器 Windows Explorer 实机安全验收已经完成，包括 no-staging、cycle/staging、持久化 RecoverySession、Forward crash reconciliation、Rollback crash reconciliation、stale Preview、missing icon 与 Auto Arrange fail-closed。

当前唯一未完成的阶段级实机项是：在真实双显示器环境中改变显示器排列、DPI/缩放或断开目标副屏后，验证旧执行候选必须 fail-closed 且 Writer 为 0。

由于当前开发环境处于旅行场景，仅有单显示器可用，无法在不伪造物理条件的情况下完成该验收。

## 2. 决策

M2 在 roadmap 中继续保持“进行中”，不提前标记 Completed。

同时允许开始 M3 的只读感知与建议模式实现，以避免物理双屏环境成为纯开发节奏阻塞。

该安排不是跳过 M2，而是将 M3 拆分为：

```text
M3-A：只读感知 / Diff / Suggestion / Notification
M3-B：无人值守真实执行
```

在真实双屏 topology acceptance 通过前，仅允许推进 M3-A；M3-B 的真实 Writer 保持关闭。

## 3. 当前允许推进的 M3 能力

- 托盘常驻基础；
- Explorer / 文件系统 / Windows 事件作为“变化提示”；
- debounce；
- 稳定等待；
- Shell Reader 完整重读；
- 当前状态快照与 Diff；
- 内部变化识别基础；
- 自动选择模板与规则的只读逻辑；
- Suggestion 模式；
- 用户通知；
- 自动化历史与诊断；
- 冷却期、暂停状态和熔断状态模型，但不触发无人值守 Writer。

事件监听不得被当作最终事实来源。任何候选都必须以稳定后的 Shell Reader 完整重读为准。

## 4. 临时禁止项

真实双屏 topology acceptance 补完前，禁止：

- 无人值守调用真实 Shell Writer；
- 自动模式在无人工确认时执行 M2 Candidate；
- 因事件监听结果直接生成物理写入；
- 绕过 M2 的 Preview / Candidate / ScopeGate / Coordinator / SafetySnapshot / Verification / Recovery 链路；
- 将双屏 topology acceptance 标记为通过；
- 将 M2 roadmap 状态改为“已完成”。

Suggestion 模式即使自动生成候选，也必须停在用户确认之前。

## 5. 解除临时门禁的验收条件

在真实双显示器 Windows 11 Explorer 环境完成：

```text
1. 扩展模式连接双屏；
2. 生成可执行 Preview；
3. 在执行前改变显示器排列、工作区、DPI/缩放或断开目标副屏；
4. 尝试执行旧候选；
5. fresh reread / topology comparison 必须拒绝；
6. Writer 必须为 0；
7. UI 要求重新 Preview / 重新确认；
8. 恢复显示器拓扑后重新生成候选可正常工作。
```

通过后：

- M2 标记为正式 Completed；
- 更新 `docs/milestones/M2-closeout.md`；
- 允许进入 M3-B 无人值守真实执行的安全门禁实现和实机验收。

## 6. 设计原则不变

该临时决策不改变冻结架构：

```text
Windows / filesystem event
→ debounce / stability
→ Shell full reread
→ state diff
→ template / rules
→ candidate
→ automation gate
→ safety snapshot
→ Writer
→ independent verification
→ history / notification / recovery
```

其中在临时门禁期间，链路最多运行到 Candidate / Suggestion，不进入真实 Writer。

核心原则继续保持：

> Observe → Compare → Act，never guess.
