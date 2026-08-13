# M5-07.6 AI Product Integration + Live Provider Gate

## 目标

把 M4 已冻结的首次桌面理解能力真正接入 Tessel WinUI 产品壳，并补齐真实 OpenAI-compatible Provider 的发布前 Physical Gate。

本阶段不是新增 AI Core。M4-01～M4-06 的 Provider、语义合同、隐私边界、Policy 与故障语义保持不变。

## 产品链路

```text
AI Assistant
    ↓ explicit user action
DesktopProductSettings
    ↓
DesktopProductIntelligenceOptionsFactory
    ↓
Fresh WindowsDesktopObservationSource
    ↓ read-only semantic facts
DesktopItemFacts + active logical Zones
    ↓
DesktopFirstRunUnderstandingRequest
    ↓
OpenAiCompatibleDesktopFirstRunIntelligenceProvider
    ↓
DesktopUnderstandingDraft
    ↓
WinUI presentation only
```

AI Disabled 在 fresh desktop observation 之前直接停止，因此不会因为打开 AI 页而偷偷读取桌面或发起网络请求。

## 产品范围

- AI 页首次进入时才 lazy hydrate；
- 用户显式点击“分析当前桌面”才执行 fresh observation 与 Provider 请求；
- 可携带最多 4096 字符的本轮用户修正；
- 成功结果展示 DesktopSummary、IntentSummary、Categories、Zones 与 Questions；
- 下一轮只复用上一轮有界 IntentSummary，不累积无限聊天历史；
- 当前 Active Layout 的 ZoneId + Name 可作为受控 existing-zone context；
- StableId 只用于本地请求映射，M4-02 Provider 继续只外发临时 item_ref；
- 离开 AI 页、隐藏到托盘、卸载 AI Surface 或显式退出时取消活动请求；
- AI Surface 在隐藏到托盘时释放，避免改变 M5-07 resident baseline。

## 安全边界

本阶段不：

- 构造 DesktopShellIconPositionWriter；
- 生成 WriterCommand；
- 调用 M2 physical planner / execution coordinator；
- 生成物理坐标；
- 移动、删除或归档文件；
- 将 AI confidence 升级成执行 authority；
- 修改 Recovery safety semantics。

成功结果仍只是 `DesktopUnderstandingDraft`。

## Live Provider Physical Gate

真实 Windows 11 x64 上至少验证：

1. 正确 Base URI / Model / Key：fresh desktop analysis 成功；
2. 错误 Key：认证失败可见，进程不崩溃；
3. 错误 Model：Provider failure 可见，不伪造 Draft；
4. 不可达 Base URI：有界失败，UI 恢复可操作；
5. 请求中取消 / 离开页面：请求停止，无后台异常；
6. AI Disabled：Provider 0 次，fresh observation 0 次；
7. 重启 Tessel：Base URI / Model / SecretReference 保持，DPAPI Key 仍可解析并用于真实请求；
8. settings.json / SQLite / 普通日志无 API Key 明文；
9. 全分析链 0 Writer、0 真实桌面图标移动；
10. Provider 返回非法 JSON / 语义越界：InvalidResponse，fail closed。

## 当前 Gate

```text
M4 Intelligence Core:          FROZEN / EXISTING PASS
M5 AI Configuration:           EXISTING PASS
AI Product Surface:            IMPLEMENTING
Real Provider Physical Gate:   PENDING
Secret Persistence Gate:       PENDING LIVE RETEST
AI Safety Boundary:            PENDING PRODUCT RETEST
M5-07.6:                       IN PROGRESS
```
