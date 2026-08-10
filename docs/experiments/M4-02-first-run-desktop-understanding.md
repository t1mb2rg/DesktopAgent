# M4-02 First-Run Desktop Understanding

## 目标

M4-02 建立 DesktopAgent 第一次使用时的整体桌面理解能力。

这一阶段允许 AI 比日常分类更主动：它可以结合当前桌面项目和用户自然语言意图，提出一份完整但仍需本地校验 / 用户确认的组织草案。

M4-02 不创建最终执行计划，也不让模型决定物理坐标。

## 输入边界

`DesktopUnderstandingRequest` 当前包含：

- 1..256 个 `DesktopItemFacts`；
- 可选 `UserIntent`，上限 4000 字符。

超过 256 个项目时不静默截断。未来 first-run orchestrator 必须显式分批或采用摘要策略。

Provider 请求只发送：

- 临时 `item_ref`（`i1`、`i2` ...）；
- DisplayName（单项最多发送 512 字符）；
- FileExtension（最多 64 字符）；
- ItemKind；
- system / filesystem flags；
- 用户当前输入的组织意图。

StableId 不发送到模型。

M4-02 仍不读取文件正文。内容分析由 M4-05 的显式开关处理。

## 输出：语义草案，不是坐标

模型只能从有限布局模式中选择：

- `Grid`
- `Columns`
- `Rows`
- `PrimaryLeft`
- `PrimaryRight`
- `PrimaryTop`
- `PrimaryBottom`

同时提出：

- `summary`；
- 最多 8 个逻辑 Zone proposal；
- 每个输入 `item_ref` 的 Zone assignment；
- 最多 3 条澄清问题；
- confidence。

模型协议中没有 `NormalizedRect`、像素坐标、Shell command 或 Writer command。

## AI 如何间接影响布局

AI 只选择有限的逻辑布局模式以及 Zone 顺序。

`DesktopZoneLayoutPatternGeometry` 在本地确定性地把该模式转换为 M2 已有 `NormalizedRect`：

```text
AI: PrimaryRight + [Development, Course, Temporary]
                    ↓
local deterministic geometry
                    ↓
Development = 右半区
Course / Temporary = 左半区确定性分割
                    ↓
existing M2 Zone / slot planner
                    ↓
current display work area / DPI / icon spacing
                    ↓
physical icon coordinates
```

因此 AI 可以表达“主要工作区放右边”这种意图，但不能生成任意 0.731284 比例或旧显示器像素坐标。

`DesktopZoneLayoutPatternGeometry` 当前最多处理 8 个 Zone，并保证生成的矩形合法且互不重叠。

## 完整 assignment

首次理解结果要求每个输入 `item_ref` 恰好出现一次。

如果模型不确定，Prompt 要求仍然使用合理的 fallback Zone，而不是静默遗漏项目。

本地会拒绝：

- 未知 layout pattern；
- 超过 8 个 Zone；
- 重复 Zone key；
- 未知 item_ref；
- 重复 item_ref；
- assignment 指向不存在的 Zone；
- assignment 数量与输入项目不一致；
- 超过 3 条问题；
- confidence / summary / key / name / purpose 超界。

## 共享传输层整理

M4-02 开始需要第二种 AI 能力，因此把 M4-01 中重复风险最高的网络部分提取为内部 `OpenAiCompatibleChatClient`。

共用层只负责：

- Secret resolution；
- `/chat/completions`；
- JSON mode；
- timeout / cancellation；
- HTTP / response stream failure；
- 256 KiB 响应限制；
- `choices[0].message.content` 提取。

单项目分类和首次整桌理解各自只负责自己的 Prompt、输入 schema 和本地结果校验。

这样后续 M4-05 自然语言 RuleDraft 也可以复用同一网络层，而不会复制第三套 HTTP 生命周期。

## 与 User Profile 的边界

M4-02 产生的是 provisional proposal。

它还不会直接：

- 写入正式 User Profile；
- 保存 learned rule；
- 创建不可撤销长期 Zone；
- 调用 Writer。

用户确认 / 修正以及把结果沉淀成 Profile / Evidence / Rules 属于 M4-03 / M4-04 的后续工作。

## 测试策略

新增测试保持紧凑：

- 一个测试遍历全部布局模式与 1..8 Zone 数，验证本地几何不重叠；
- 一个测试覆盖 request 显式上限；
- 一个 success 测试同时覆盖 ephemeral item refs、StableId 不外发、UserIntent 和 Proposal 映射；
- 一个 invalid 测试组覆盖自由布局、未知 item_ref 和未知 Zone。

M4-01 已经覆盖共享 transport 的认证、HTTP、timeout、response limit 等行为，因此 M4-02 不再重复同一批传输测试。

## 本阶段仍不做

- 不做 first-run 正式 UI；
- 不把 Proposal 自动保存成模板；
- 不做多轮对话 orchestrator；
- 不写 SQLite User Profile；
- 不读文件正文；
- 不输出物理坐标；
- 不修改 M2 / M3 / Recovery / Writer。
