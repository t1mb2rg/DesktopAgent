# M5-04 First-Run + Settings + AI Configuration

## 目标

把 M5-03 的 Settings 安全占位升级成可持久化的产品设置入口，同时复用 M4 已冻结的 OpenAI-compatible Provider 与 SecretReference 边界。

M5-04 不打开 Writer，也不把 AI 配置解释成执行授权。

## Product Settings

产品设置包含：

- Automation Mode：Manual / Assisted / Automatic；
- FirstRunCompleted；
- AI Enabled；
- OpenAI-compatible BaseUri；
- Model；
- SecretReference。

首次启动默认：Assisted、FirstRun 未完成、AI Disabled。

小型 UI 偏好保存在 `%LocalAppData%\DesktopAgent\settings.json`，使用 formatVersion=1 与临时文件替换保证写入边界。桌面布局、恢复、分类、Pending Queue 等领域状态继续由 SQLite 管理。

## Secret

真实 API Key 永远不进入 `DesktopProductSettings` 或 settings.json。

Windows 产品壳使用 CurrentUser DPAPI：

- settings.json 只保存 SecretReference；
- `%LocalAppData%\DesktopAgent\secrets` 只保存 DPAPI 保护后的 blob；
- PasswordBox 不回显已保存 API Key；
- 空 PasswordBox 表示保留现有 Key；
- 用户可以显式删除已保存 Key。

本地无认证 OpenAI-compatible 服务允许 SecretReference=null。

## First Run

当 FirstRunCompleted=false：

- 产品启动后自动进入 Settings；
- 显示首次设置 InfoBar；
- 用户选择 Automation Mode；
- AI 可保持关闭，也可配置 BaseUri / Model / 可选 API Key；
- 点击“完成首次设置”后持久化 FirstRunCompleted=true。

完成首次设置不调用 AI、不扫描桌面、不生成 WriterCommand。

## 安全边界

- Automatic 不提升 AI authority；
- AI-only 仍遵守 M5-01 Policy，不能直接 AUTO；
- 保存设置不会进行网络请求；
- API Key 不进入 SQLite、JSON、日志或 UI 回显；
- 不调用 Writer；
- 不修改 Recovery safety semantics；
- 不进入 Layout Editing。

## Physical Windows Gate

CI 通过后在真实 Windows 11 x64 验证：

1. 新用户状态启动后自动进入首次设置；
2. Manual / Assisted / Automatic 可保存并在重启后恢复；
3. AI Disabled 可以完成首次设置；
4. AI Provider BaseUri / Model 可保存并恢复；
5. API Key 保存后 PasswordBox 为空，仅显示“已安全保存”；
6. settings.json 不含 API Key 明文；
7. 重启后 Key 仍可由 DPAPI Store 解析；
8. 删除 Key 后状态更新且无法再次解析；
9. 旧的四页导航、单实例、托盘生命周期不回归；
10. 全程没有 Writer / unattended desktop movement。
