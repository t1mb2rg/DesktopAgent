# M4 Entry Contract

## 目的

M4 从已经完成实机验收的 M3-B automatic safety chain 之上继续演进。

M4 可以增加更高层的智能、策略、长期规划、用户偏好与自动整理决策，但不获得新的桌面写入捷径。

## 唯一允许的执行方向

```text
M4 intent / policy / planner
→ M3 Observation / Suggestion lifecycle
→ M3 Runtime / risk / preflight
→ B01 fresh Observation + fresh Candidate
→ B02 automatic safety
→ B03 existing M2 Coordinator pre-writer gate
→ safety snapshot / recovery checkpoint / Writer / verification
```

M4 不直接调用 Writer，也不重新实现一套 M2 execution pipeline。

## 不可回退的架构原则

1. 当前桌面事实来自 fresh Shell Observation，而不是 Store 历史。
2. Store 记录意图、历史和证据，不负责证明“现在桌面是什么”。
3. 旧 Candidate 只能作为历史计划证据，不是 execution authorization。
4. 每次 automatic attempt 都必须重新读取现实并重新规划。
5. Display topology、Auto Arrange、IconSpacing、template/rules、recovery、runtime/lifecycle 任一变化都可使旧票失效。
6. 安全停止不是 execution failure；只有真实 Coordinator result 才进入 execution outcome feedback。
7. 一旦进入 M2 transaction，则按现有 snapshot/checkpoint/verification/rollback 规则完整收口。
8. Hint / timer /模型输出本身永远不是 Writer 授权。

## M4 输出应是什么

优先输出：

- intent
- policy choice
- template / target selection
- priority / confidence / user-facing reason
- scheduling / eligibility hints
- constraints / risk preference

而不是：

- raw Writer command
- cached authorized execution plan
- 绕过 fresh boundary 的绝对坐标动作

## 显示器身份

B07-E 实测虚拟副屏在重新连接后可能获得新的 `DISPLAYxx` DeviceName。

因此若 M4 引入跨 session 的显示器偏好或长期布局记忆，应使用可重新解析的 display identity / topology fingerprint 设计，而不是把瞬时 DeviceName 作为永久主键。

在没有更强 identity 方案前，执行时始终以 fresh topology 中当前可解析的 target 为准；无法解析则 fail closed。

## M4 最低回归要求

任何会改变 automatic eligibility / planning 的 M4 增量，至少需要证明：

- stale current facts 不会使用旧 Candidate 写桌面；
- target display missing 时 Writer=0；
- unresolved recovery 时 Writer=0；
- runtime/lifecycle revision stale 时 Writer=0；
- M4 不新增 `IDesktopIconPositionWriter` 直接依赖；
- 成功/失败仍由现有 M3-B runtime cooldown/fuse 反馈链处理。

## 非目标

M4 entry 本身不要求：

- 普通 PoC / Release 默认打开 unattended gate；
- 重写 M2 Planner；
- 重写 Recovery；
- 更换 Writer；
- 把 acceptance-only failure injection 带入生产 composition。

这些若未来需要变化，必须单独设计、单独验收。