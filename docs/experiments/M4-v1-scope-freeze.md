# M4 v1.0 Scope Freeze

## 目的

M4 是 DesktopAgent v1.0 在 M5 产品化之前的最后一个核心能力里程碑。

M4 的目标不是把 DesktopAgent 扩展成通用 AI Agent 或通用文件管理器，而是在已经完成的 M0–M3 安全执行底座之上增加一层可学习、可解释、可替换 Provider 的桌面智能。

M4 完成后进入 v1.0 Core Feature Freeze。M5 主要负责正式产品 UI、安装发布、兼容性、性能、可用性和预发布验收，不再无边界追加核心能力。

## 产品定位

DesktopAgent v1.0 是一个轻量、AI-first、直接作用于 Windows Explorer 原生桌面图标布局的智能桌面管理工具。

它应当做到：

```text
Understand → Organize → Maintain
```

真实文件移动、归档、删除和完整 Desktop Clean / Archive / Restore 生命周期保留给后续版本。

## 总原则：大胆 Intelligence，保守 Execution

M4 的智能层允许主动理解、提出分类、推荐 Zone、总结偏好、生成 RuleDraft 和探索更好的桌面组织方式。

但 M4 不获得任何新的桌面写入能力。

```text
M4 Intelligence / Profile / Policy
→ existing M2 / M3 planning and automatic chain
→ fresh Observation + fresh Candidate
→ existing M2 Coordinator
→ Snapshot / Recovery / Writer / Verification
```

AI、Prompt、历史行为或长期记忆本身永远不是 Writer 授权。

M0–M3 已冻结的 fresh-boundary、Recovery、Writer、Cooldown 和 Fuse 语义继续保持。

## AI Provider 策略

v1.0 使用通用 OpenAI-compatible 协议边界，而不是把 Core 绑定到某一家模型提供方。

默认产品体验可以推荐 DeepSeek，但用户应能够配置：

- Base URL；
- API Key；
- Model；
- 兼容协议所需的少量高级参数。

这样同一条产品链路可以连接：

- DeepSeek 云 API；
- 兼容 OpenAI 协议的其他云 API；
- 本地 vLLM；
- 自建网关或转发服务。

v1.0 只实现 DesktopAgent 所需的保守协议子集，不追求覆盖 OpenAI API 的全部能力。

Python 不作为 v1.0 产品运行时依赖。Python 可以继续用于 Prompt 实验、模型评估和未来本地模型探索。

## 内容分析与隐私

内容分析必须是显式可控能力。

默认的轻量分类上下文优先使用：

- 文件名 / DisplayName；
- 扩展名；
- ItemKind；
- 快捷方式等可安全提取的基础事实；
- 已确认规则；
- 本地用户偏好。

当用户显式开启内容分析后，DesktopAgent 才可以读取支持类型的文件内容用于增强分类。

第一版优先采用“本地提取必要文本 → 限长 / 截断 → 发送必要上下文”，而不是默认上传完整原文件。

图片、OCR 和多模态内容理解不作为 v1.0 完成条件。

## 首次使用与日常调用策略

首次使用是 Desktop Understanding 阶段，AI 可以高频参与：

```text
读取当前桌面
→ AI 理解项目与潜在组织方式
→ 用户确认 / 修正
→ 继续理解
→ 建立初始 Rules / Preferences / Evidence
→ 初始化完成
```

日常运行则优先复用已经沉淀的本地知识：

```text
新项目 / 新变化
→ 已确认规则足够？        → 本地判断
→ 已有偏好足够？          → 本地 / 轻量判断
→ 仍有语义不确定性？      → 调用 AI
→ 用户反馈
→ 形成新的行为证据
```

因此 DesktopAgent 应当随着使用逐渐更懂用户，同时避免对每个已知问题重复消耗 AI 请求。

## 本地 User Profile 与学习

v1.0 的长期记忆由 DesktopAgent 自己拥有，不能依赖某一家模型 Provider 的私有 Memory。

第一版不需要向量数据库或完整语义记忆系统。SQLite 中优先维护可审计的本地 Profile：

- Preferences；
- Rules；
- ClassificationHistory；
- BehaviorEvidence；
- Learned RuleDrafts。

历史行为是证据，不自动等于规则。

重复行为可以形成 Preference 或 RuleDraft 建议，但正式长期规则仍需要明确的产品策略或用户确认，不做静默黑箱自学习。

Provider-side memory、embedding 检索、RAG 和长期语义记忆保留给后续版本探索。

## M4 子阶段

### M4-00 Intelligence Domain Contract

建立 UI、网络和 Persistence 无关的核心契约，包括：

- Desktop item intelligence facts；
- classification suggestion；
- policy-resolved classification decision；
- suggestion / decision 来源；
- 后续 Preference、Evidence、RuleDraft 的可扩展边界。

M4-00 不连接真实模型，不写 SQLite，不修改 M2/M3 执行链。

### M4-01 OpenAI-Compatible Intelligence Gateway

建立 UI-agnostic Application contract 和 OpenAI-compatible Provider：

- Base URL / Model / Secret 引用；
- timeout / cancellation；
- 结构化响应；
- invalid response / unavailable / timeout 的明确结果；
- Provider 不得输出 Writer command 或物理坐标动作。

### M4-02 First-Run Desktop Understanding

实现首次桌面理解工作流：

- 批量理解现有桌面项目；
- 主动提出分类和 Zone 组织建议；
- 接受用户修正；
- 建立初始 Profile；
- 允许较高 AI 调用频率，而不直接执行未经现有安全链验证的写入。

### M4-03 Classification + Policy Engine

组合：

- 用户明确规则；
- 本地确定性规则；
- 本地 User Profile；
- AI semantic suggestion。

输出可解释的 ClassificationDecision，并明确冲突、来源和最终采用理由。

具体 UI 呈现方式在开发过程中逐步确定，不在 M4 Scope Freeze 中提前锁死。

### M4-04 Local User Profile & Learning Evidence

使用 SQLite 保存：

- 用户偏好；
- 分类历史；
- AI 建议与最终决定；
- 用户接受 / 修正行为；
- 形成 RuleDraft 所需的行为证据。

Store 仍然记录历史和证据，不把模型推断当作当前 Shell 事实。

### M4-05 Natural Language Rules + Content Analysis

实现：

- 自然语言 → 结构化 RuleDraft；
- 用户可检查 / 修改 / 保存；
- 内容分析隐私开关；
- 支持类型的限量本地文本提取；
- AI 输出的结构、Zone、规则条件全部由本地代码验证。

### M4-06 End-to-End Acceptance + Feature Freeze

完成完整智能桌面闭环及故障验收：

- API 正常；
- API 不可达；
- timeout / cancellation；
- malformed / invalid structured output；
- 不存在的 Zone；
- 用户规则覆盖低优先级 AI 建议；
- 内容分析关闭时不读取 / 发送正文；
- AI 错误不能绕过 M2/M3 安全链；
- stale facts / missing display / unresolved recovery 等既有 fail-closed 语义保持。

通过后进入：

```text
v1.0 CORE FEATURE FREEZE
```

## UI 与 M5 边界

M4 新能力必须通过 UI-agnostic Application / Core contract 暴露，不能把 WPF-specific 语义写入核心业务层。

当前方向：

- WPF 逐步整合为 Developer / Diagnostics Workbench；
- M5-00 建立隔离 WinUI 3 壳并完成正式可行性决策；
- 若 WinUI 3 验证通过，它作为面向普通用户的简洁 Product UI；
- WPF 不作为第二套完整产品 UI，而保留底层状态、诊断、验收和开发入口。

WinUI 3 具体页面和展示内容在开发过程中讨论，不在 M4 阶段过早冻结。

## v1.0 明确非目标

M4 v1.0 不要求：

- 向量数据库；
- embedding 长期记忆；
- Provider-owned memory 作为核心依赖；
- 正式本地模型 Runtime；
- 产品安装包携带 Python Runtime；
- Multi-Agent；
- RAG；
- 完整文件知识库；
- 图片 / OCR / 多模态桌面理解；
- AI 生成任意脚本并执行；
- 静默创建不可审计长期规则；
- 删除、移动或归档真实文件；
- 管理整个磁盘、Downloads 或 Documents；
- 把 DesktopAgent 扩展成通用 AI 文件管理器。

这些能力若未来值得继续，应在 v1.x / v2+ 以独立安全模型和里程碑重新设计。
