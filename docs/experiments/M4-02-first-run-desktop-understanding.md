# M4-02 First-Run Desktop Understanding

## 目标

M4-02 让 DesktopAgent 在第一次使用阶段主动理解整张桌面与用户的组织意图，而不是只对单个项目做孤立分类。

本阶段建立的是 UI 无关的 Intelligence capability。WPF / WinUI 3 首次引导界面留到后续产品流程中消费这些契约，不把 UI 语义写进 Core / Application。

## 为什么首次使用允许更高频调用 AI

第一次使用时，DesktopAgent 尚未拥有足够的本地 Rule / Preference / Behavior Evidence。此时频繁调用 AI 的价值不是重复分类，而是快速建立：

- 用户如何理解自己的桌面；
- 哪些语义 Category 长期存在；
- 哪些 Category 适合共享或独立 Zone；
- 用户希望不同内容大致位于哪里；
- 哪些问题仍需要用户明确回答。

后续 M4-04 会把经过确认的偏好和行为证据沉淀到本地 Profile。日常阶段再逐渐减少不必要的 AI 调用。

## 有界迭代，而不是无限聊天历史

每一轮首次理解请求只携带：

```text
current desktop facts
current existing zones
previous CurrentIntentSummary
latest LatestUserGuidance
```

模型返回新的：

```text
IntentSummary
DesktopSummary
CategoryDraft[]
ZoneDraft[]
Assignment[]
Question[]
```

下一轮使用新的 `IntentSummary` 和用户本轮修正继续，而不是把完整对话逐轮追加。

这样保留“连续理解用户”的体验，同时让 token、内存与请求体不会随着会话长度无界增长。

当前 v1.0 输入上限：

- 512 个桌面项目；
- 32 个已有 Zone；
- IntentSummary 8192 字符；
- LatestUserGuidance 4096 字符。

当前输出上限：

- 16 个 Category；
- 16 个 Zone；
- Assignment 不超过本轮输入项目数；
- 最多 3 个澄清问题；
- OpenAI-compatible transport 仍有 256 KiB 响应正文硬上限。

## 临时引用与隐私

`DesktopItemFacts.StableId` 对本地状态关联有价值，但对模型理解“这个项目是什么”没有语义价值，因此 M4-02 不把它发送到 Provider。

每轮调用临时建立：

```text
stable local identity
        ↓ local map
item-0001 / item-0002 / ...
        ↓
AI
        ↓
item_ref
        ↓ local map
stable local identity
```

已有 Zone 也采用相同方式：

```text
real ZoneId Guid
        ↓ local map
existing-zone-0001
        ↓
AI
        ↓
reuse_existing_zone_ref
        ↓ local map
real ZoneId Guid
```

因此模型不需要知道真实 StableId 或 Zone Guid。

本阶段仍不读取或发送文件正文；内容分析开关属于 M4-05。

## AI 如何提出布局

AI 不输出物理坐标，也不直接输出 `NormalizedRect`。

`DesktopUnderstandingZoneDraft` 只允许有限语义提示：

### PlacementHint

- Unspecified
- Left
- Right
- Top
- Bottom
- Center
- TopLeft
- TopRight
- BottomLeft
- BottomRight

### SizeHint

- Flexible
- Compact
- Medium
- Large

例如 AI 可以表达：

```text
Development
placement = Right
size = Large
```

但不能表达：

```text
x = 1832
y = 420
width = 600
height = 900
```

后续确定性本地链路仍是：

```text
semantic Zone intent
        ↓
local Policy / layout compiler
        ↓
validated logical Zone geometry
        ↓
existing M2 Zone / slot planner
        ↓
physical coordinates
        ↓
existing M3 / Coordinator safety chain
```

所以显示器分辨率、DPI、拓扑或工作区变化不会使 AI 学到的用户语义意图失效。

## Core 草案

M4-02 新增纯语义结构：

- `DesktopZonePlacementHint`
- `DesktopZoneSizeHint`
- `DesktopUnderstandingCategoryDraft`
- `DesktopUnderstandingZoneDraft`
- `DesktopUnderstandingAssignment`
- `DesktopUnderstandingQuestion`
- `DesktopUnderstandingDraft`

`DesktopUnderstandingDraft` 自身会检查 Category / Zone / Assignment 交叉引用与重复键，但没有任何执行权限。

## Application capability

新增：

- `DesktopFirstRunUnderstandingRequest`
- `IDesktopFirstRunIntelligenceProvider`
- `DesktopFirstRunUnderstandingResult`
- `OpenAiCompatibleDesktopFirstRunIntelligenceProvider`

Provider 可以：

- 理解整张桌面；
- 根据上一轮摘要和最新用户指导修正理解；
- 建议 Category；
- 建议复用已有 Zone 或提出新逻辑 Zone；
- 给项目做语义 Assignment；
- 提出有限澄清问题。

Provider 不能：

- 写物理坐标；
- 生成 Shell command；
- 移动或删除文件；
- 调用 Writer；
- 绕过 M2 / M3 / Recovery。

## M4-01 代码卫生

M4-02 会同时消费单项目分类和整桌理解，因此不复制第二套 HTTP 实现。

M4-01 中以下通用能力已抽成内部 `OpenAiCompatibleJsonChatTransport`：

- SecretReference 解析；
- Bearer auth / 本地无认证；
- `/chat/completions` 请求；
- JSON mode；
- timeout / caller cancellation；
- HTTP failure；
- response stream failure；
- 256 KiB response limit；
- 8 KiB `ArrayPool<byte>` 读取缓冲；
- `choices[0].message.content` 提取。

单项目 Provider 和 first-run Provider 只负责各自的语义 prompt、payload 和本地业务校验。

## 测试策略

M4-02 不复制已有 transport 测试。

新增测试集中验证新的独立不变量：

1. 首次理解请求必须是有界且身份唯一的工作集；
2. StableId / ZoneId 不外发，临时 ref 可以正确映射回本地身份；
3. 上一轮摘要与本轮用户修正确实进入下一次 AI 请求；
4. unknown item_ref、unknown existing_zone_ref、非法 semantic hint 会被本地拒绝；
5. Core Draft 不接受断裂的 Category / Zone / Assignment 语义图。

M0-M3 安全回归测试继续保留。

## M4-02 明确不做

- 不实现具体首次引导 UI；
- 不持久化 User Profile；
- 不自动把 AI Draft 升级成长期 Rule；
- 不生成真实 Zone 几何；
- 不读取文件正文；
- 不做 embedding / vector memory；
- 不接 Writer；
- 不打开 production unattended gate。
