# ADR-0003：引入 Application 层并隔离 M3 只读 Observation 与 Writer

- 状态：已接受
- 日期：2026-08-08

## 背景

M0–M2 的主要交互形态是用户在 WPF 中主动发起读取、预览和执行，因此应用级编排逻辑尚未形成独立项目。

M3 开始后将出现常驻后台生命周期、事件提示、防抖、稳定等待、重复观察、状态 Diff、Suggestion、暂停、冷却、熔断和后续自动化授权等长生命周期用例。如果继续由 WPF Window / code-behind 承载，会把业务编排与表现层绑定，并扩大后台路径意外获得 Shell Writer 的风险。

M2 实机验收同时再次确认：

> 当前物理事实必须来自 fresh observation；事件、日志、旧 Preview 和历史意图都不能替代当前 Shell observation。

## 决策

新增 `DesktopAgent.Application` 项目作为应用用例与后台编排层。

长期依赖方向为：

```text
DesktopAgent.Core
        ↑
DesktopAgent.Application
        ↑
WindowsInterop / Persistence 等基础设施实现 Application ports
        ↑
WPF / 后续其他表现层负责组合与交互
```

其中：

- Application 可以引用 Core；
- Application 不引用 WPF、WinUI、Shell COM、Win32、SQLite 或 WindowsInterop；
- WindowsInterop 可以引用 Application，以实现 Application 定义的系统能力 port；
- WPF 负责 DI 组合、展示和用户交互，不承载 M3 的核心后台状态机。

## M3 Observation 边界

M3 的只读感知路径使用 `IDesktopObservationSource`，成功结果为完整 `DesktopObservation`。

一次成功 Observation 至少包含：

- 完整显示器拓扑；
- Auto Arrange / IconSpacing 定位能力；
- Shell Reader 完整桌面项目；
- StableId 与显示器归属；
- observation 时间与读取诊断元数据。

以下情况不得生成成功 Observation：

- 桌面读取不完整；
- 显示器身份不唯一；
- StableId 不唯一；
- 图标无法归属于当前 topology；
- topology 在一次 capture 前后发生变化；
- 底层读取或模型构建失败。

事件监听只能触发“值得重新观察”，不得直接成为 DesktopObservation 的事实来源。

## Writer 隔离

将只读定位能力抽象为：

```text
IDesktopIconPositioningCapabilitiesReader
    GetCapabilities()
```

`IDesktopIconPositionWriter` 继续作为该接口的超集，以保持 M1/M2 兼容，但 M3-A Observation 路径只允许依赖 capability reader，不允许依赖 `IDesktopIconPositionWriter` 或真实执行 Coordinator。

因此在 M3-A 中：

```text
Event Hint
→ Observation
→ Stability
→ Diff
→ Suggestion
→ STOP
```

不存在无人值守真实写入入口。

后续 M3-B 即使解除临时门禁，也只能通过正式自动化授权门连接现有 M2 安全执行链，不允许另建旁路 Writer。

## 与 M2 → M3 临时决策的关系

本 ADR 是长期架构决定，不替代 `M2-to-M3-provisional-transition.md`。

真实双显示器 topology acceptance 通过前，临时门禁仍然禁止 unattended real Writer；本 ADR 仅保证 M3-A 当前开发路径在结构上保持只读。

## 后果

优点：

- M3 后台编排不进入 WPF code-behind；
- Observation / Diff / Suggestion 更容易进行纯测试；
- 只读路径从类型层面不获得 Writer API；
- 后续 WinUI 3 或其他表现层迁移不需要重写 M3 应用编排；
- 与 M2 的 observation-first / fail-closed 原则一致。

代价：

- solution 新增一个项目和一层接口；
- WindowsInterop 开始引用 Application 以实现系统 port；
- 后续需要明确区分 Core 纯领域比较器与 Application 长生命周期 orchestrator，避免把全部逻辑都堆入 Application。
