# M3-B07 Controlled Automatic Acceptance Composition

## 目的

M3-B01..B06 已经在自动测试中完成：

```text
Suggestion lifecycle
→ 05C
→ B01 fresh observation / fresh Candidate
→ B02 automatic safety
→ B03 existing M2 Coordinator / Writer
→ B04 cooldown / fuse
→ B05 full cycle
→ B06 PoC trigger / tray
```

普通 DesktopAgent PoC 仍固定使用 `M3AProvisionalClosed`，不会进入 unattended Writer。

B07 新增一把**独立、Debug-only、受控的实机验收点火钥匙**，用于用户回到 Windows 主力机以后验证 M3-B 真实行为。

## 三重武装

真正允许本进程进入 unattended real write 需要三步全部完成：

1. 完整设置 M3 acceptance 环境变量；
2. Debug 进程启动时，在大字 Warning 中明确点击 **Yes**，默认选择是 No；
3. 程序启动后，再从 tray 手动选择 **Automatic**。

任一步缺失都不会自动写桌面。

即使 acceptance 已启用，Runtime 初始仍然：

```text
RequestedMode = Suggestion
EffectiveMode = Suggestion
```

只有用户随后显式选择 Automatic，ValidatedOpen gate 才真正允许 05C 进入 B01。

## 环境变量

必须完整提供：

```text
DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MODE=ValidatedOpen
DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ARM=I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITES
DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_MOVES=<1..10>
DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_STEPS=<1..20>
DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ALLOW_STAGING=true|false
```

所有字符串都是严格解析：

- Mode 必须精确 `ValidatedOpen`；
- ARM 必须逐字符完全匹配；
- move / step 必须为十进制正整数且不能超过 hard cap；
- staging 必须精确小写 `true` 或 `false`；
- 任意变量缺失/错误 → fail-closed startup。

## 为什么 acceptance risk 必须显式配置

B07 不允许“打开 ValidatedOpen，但风险预算继续吃普通默认值”。

验收者必须明确写出本次愿意允许的：

- 最大移动图标数；
- 最大执行步骤数；
- 是否允许 temporary staging。

这样每次实机验收都是一个有边界的实验，而不是一把无范围的万能钥匙。

建议第一次真实 M3-B 验收：

```text
MAX_MOVES=1
MAX_STEPS=1
ALLOW_STAGING=false
```

先验证单 direct move 完整闭环，再逐步扩大。

## 与 M2 fault injection 互斥

现有 M2 fault injection：

```text
DESKTOPAGENT_ACCEPTANCE_FAULT_MODE
...
```

与 B07 M3 automatic acceptance **不能同时启用**。

如果两组变量同时武装，App 启动即 fail closed 退出。

原因：

- M2 fault injection 是故意在 Writer 窗口制造异常/进程终止；
- M3 acceptance 是允许后台 unattended real write；
- 两者混用会让验收目的、退出语义和人工恢复判断互相污染。

需要测试 M3-B 的 Writer crash/recovery 时，应另设计明确的组合验收场景，而不是把两把现有钥匙一起插上。

## Release 永远不读取 M3 acceptance 环境变量

`M3AutomaticAcceptanceOptions.FromEnvironment()` 只在 `#if DEBUG` App startup 中调用。

Release composition 永远沿用普通：

```text
M3AutomationPoCDefaults.CreateRuntimePolicy()
→ M3AProvisionalClosed
```

因此这些环境变量不是生产功能开关。

## PowerShell：第一次最小风险武装

仅在准备实机验收的 PowerShell 会话中：

```powershell
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MODE = 'ValidatedOpen'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ARM = 'I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITES'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_MOVES = '1'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_STEPS = '1'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ALLOW_STAGING = 'false'
```

然后从同一 PowerShell 会话启动 Debug DesktopAgent。

启动后还要：

1. 阅读 M3 unattended write warning；
2. 点击 Yes；
3. 建立/确认 Suggestion binding；
4. 启动 monitoring；
5. 确认当前桌面没有 unresolved M2 recovery；
6. 最后才从 tray 选择 Automatic。

## PowerShell：解除武装

验收完成后立即在该会话执行：

```powershell
Remove-Item Env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MODE -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ARM -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_MOVES -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_STEPS -ErrorAction SilentlyContinue
Remove-Item Env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ALLOW_STAGING -ErrorAction SilentlyContinue
```

不要把 ARM 变量写入长期系统环境变量或启动脚本。

## 建议的真实验收顺序

### A. 单 direct move

```text
MAX_MOVES=1
MAX_STEPS=1
ALLOW_STAGING=false
```

验收：

- 新 Active Suggestion 出现；
- 用户切 Automatic；
- B05 fresh reread；
- Writer 只执行一次预期移动；
- Coordinator Completed；
- Runtime 进入 cooldown；
- monitoring 随后观察真实桌面并使旧 Suggestion 自然失效/NoActionNeeded；
- 无 unresolved recovery。

### B. stale window

在 Suggestion 出现后、Automatic commit point 前人工改变图标或显示拓扑。

预期：

```text
B01 / Coordinator fresh planner fail closed
→ Writer=0
```

### C. cooldown / duplicate trigger

一次成功后保持同一 Suggestion/event 噪声。

预期：

- 不重复自动写；
- cooldown 期间 Effective Suggestion；
- event trigger 被 dedupe/coalesce。

### D. failure / fuse

只在有明确可控失败注入方案后执行。

预期：

- failure 进入 cooldown；
- 连续 failure 达阈值后 fuse；
- fuse 不自动自愈；
- 用户显式 ResetFuse 后才重新允许。

### E. 真实双显示器拓扑

最终补上 M2 仍欠的真实双屏 acceptance，并同时验证 M3-B：

```text
Preview / Suggestion
→ 改变真实双屏排列、work area、DPI/scale 或断开目标副屏
→ old automation ticket 不得继续
→ fresh topology / Candidate / Coordinator fail closed
→ Writer=0
→ 必须基于新事实重新 Suggestion / preflight
```

## 当前状态

B07 只提供受控点火入口和验收文档。

**尚未完成真实 Windows M3-B acceptance。**

在用户实际运行上述步骤前：

- 不把 M2 real dual-monitor acceptance 标记为 Completed；
- 不把 M3-B physical acceptance 标记为 Completed；
- 普通 PoC / Release gate 继续保持 closed。
