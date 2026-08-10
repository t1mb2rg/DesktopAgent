# M4-01 OpenAI-Compatible Intelligence Gateway

## 目标

M4-01 建立 DesktopAgent v1.0 的第一条真实 AI Provider 边界，但仍不把模型输出升级为桌面执行授权。

本阶段只回答：

> DesktopAgent 如何用一个足够通用、轻量、可测试的协议，把桌面语义事实交给模型并得到本地可验证的分类 Suggestion？

## 协议选择

v1.0 采用 OpenAI-compatible Chat Completions 的保守子集：

```text
POST {BaseUri}/chat/completions

model
messages
response_format = { type: "json_object" }
stream = false
```

选择 `json_object` 而不是把 v1.0 强绑定到更窄的 Structured Outputs / JSON Schema 实现，是为了优先覆盖当前 DeepSeek、OpenAI-compatible 网关和本地 vLLM 等常见路径。

DesktopAgent 仍会在本地重新解析和校验每一个返回字段，因此 Provider 对“返回合法 JSON”的保证不等于应用层信任。

## Provider 配置

`OpenAiCompatibleProviderOptions` 当前只保存：

- `BaseUri`；
- `Model`；
- 可选 `SecretReference`；
- 有界 `Timeout`。

实际 API Key 不属于该配置对象。

有认证的云 Provider：

```text
SecretReference
→ IIntelligenceSecretResolver
→ transient API key
→ Authorization: Bearer ...
```

无认证的本地 vLLM / 自建服务可以将 `SecretReference` 留为 `null`，此时不发送 Authorization header。

后续 Windows Secret Store 实现可以使用 Credential Manager / DPAPI 等平台能力，但不能把明文 Key 作为普通 SQLite 配置字段。

## 请求隐私边界

M4-01 默认只发送当前分类需要的语义信息：

- `DisplayName`；
- `FileExtension`；
- `ItemKind`；
- `IsSystemItem`；
- `IsFileSystemItem`；
- 当前可选 Zone 的 `ZoneId + Name`。

`StableId` 是 DesktopAgent 本地身份信息，对语义分类没有必要，因此 M4-01 不发送它。

本阶段也不读取或发送文件正文。

## 坐标与执行边界

Provider Prompt 明确禁止输出：

- physical coordinates；
- Shell commands；
- file move / delete commands；
- scripts；
- executable actions。

更重要的是，应用层协议本身也没有这些字段。

模型只能返回：

```json
{
  "category": "Development",
  "suggested_zone_id": "existing-zone-guid-or-null",
  "confidence": 0.91,
  "reason": "..."
}
```

`suggested_zone_id` 若不属于本次请求提供的 Zone 列表，结果直接变为 `InvalidResponse`。

后续真实布局仍由：

```text
semantic suggestion
→ local Policy
→ existing M2 Zone / slot planner
→ physical coordinates
→ existing M3 / Coordinator safety chain
```

## 故障模型

单次 Provider 调用使用明确状态：

- `Success`；
- `NotConfigured`；
- `Unavailable`；
- `Timeout`；
- `Cancelled`；
- `InvalidResponse`。

非 `Success` 状态绝不携带伪造 Suggestion。

API 不可用属于智能能力降级，不等于 DesktopAgent Core / M2 / M3 故障。

## 资源边界

- `HttpClient` 由调用方管理生命周期，Provider 不为每次请求创建新的 Client；
- Provider timeout 必须有限，当前上限 5 分钟；
- HTTP 使用 `ResponseHeadersRead`，避免无条件先把整个响应缓冲到内存；
- 单次响应正文硬限制为 256 KiB；
- 长期历史不进入本 Provider 常驻内存。

这只是第一层资源约束。M5 仍会做正式的 idle / peak / long-running profiling。

## 测试策略

M4-01 测试只覆盖独立协议与故障行为，不按每个字段机械拆成大量 Fact：

- 云端认证与本地无认证配置；
- `/chat/completions` 请求形状；
- JSON mode；
- StableId 不外发；
- 正常 Suggestion 映射；
- unknown Zone 本地拒绝；
- malformed payload 本地拒绝；
- missing Secret 在 HTTP 前停止；
- HTTP unavailable；
- oversized response；
- timeout 与 caller cancellation 区分。

M0–M3 的安全回归测试不在本阶段削减范围内。

## 本阶段不做

- 不接真实用户 API Key 存储 UI；
- 不把 Provider 接进 WPF；
- 不实现 first-run desktop interview；
- 不读文件正文；
- 不写 User Profile SQLite；
- 不生成 ZoneDraft；
- 不触碰 M2 / M3 / Recovery / Writer；
- 不打开 production unattended gate。
