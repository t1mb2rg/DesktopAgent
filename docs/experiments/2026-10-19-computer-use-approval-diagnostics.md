# Computer Use 验收阻塞诊断

## 已确认事实

- 用户明确授权操作测试窗口，当前 shell 会话为 danger-full-access / approval never。
- Computer Use 初始化、list_apps 成功；launch_app 在执行前返回 `Computer Use was not approved to use winui`。用户确认没有看到授权弹窗。
- 本地安装的 `@oai/sky` Windows `internal/helper_transport.js` 中存在该报错：helper 请求应用审批后，经 createElicitation 回传；action 不为 accept 时抛出该错误。还有一个相同错误分支：策略不允许持久授权，但回传指定 always。当前未捕获回传对象，不能确定命中哪个分支。
- 当前 nodeRepl 公共对象上的 createElicitation 不是函数；requestMeta 存在 callId/itemId 和 x-codex-turn-metadata。后者不含 call_id/item_id。这是观察到的上下文形状，尚未证明是故障原因；运行时可能使用内部注入的审批回调。
- 用户报告 DeepSeek harness 能正常唤出窗口。尚未拿到该成功调用的工具、运行时版本和调用方式，不能据此确认本会话使用了相同链路。
- 本地两个历史 Computer Use session 文件分别记录了 diagnostics.exe 和另一个应用的许可，当前没有发现 winui 的对应会话许可。没有修改这些文件。

## 判断与边界

## 后续重试进展

用户再次调整环境后，本会话成功读取 `Tessel Dev` 窗口（id 130815260）的截图和可访问性树，不再出现审批拒绝。此前的读取阻塞已解除，具体由哪项环境变化解除尚未确认。

截图显示概览页；可访问性树只有窗口与标题栏。尝试点击左侧布局、显式激活后重新点击，以及发送 Tab，后续截图均未显示页面/焦点变化。不能因此直接认定业务窗口卡死或输入被权限拦截；需对照人工点击是否生效。尚未完成 F-1～F-5 的界面验收。

人工对照确认窗口能正常点击。随后只读查询进程状态和 TokenElevation：

| 进程 | PID | Responding | TokenElevation |
| --- | --- | --- | --- |
| DesktopAgent.WinUI | 36284 | True | Elevated |
| codex-computer-use-swift | 38536 | True | NotElevated |

这确认了当前目标与自动化工具之间存在提升权限差异，符合 Windows UIPI 对 SendInput 的限制：普通完整性进程不能向更高完整性进程注入输入。该事实解释当前“可读取截图但点击无效”的现象；此前审批弹窗缺失是更早发生的独立问题，不能混为一谈。

下一步由用户正常退出管理员身份的 Tessel，再以普通权限重新启动同一个 WinUI 3 可执行文件。无需提升自动化工具或修改 Windows 安全设置；之后重新获取窗口并重试输入验证。

问题已缩小到应用审批回传与当前会话/客户端之间的链路，而非 .NET 构建失败。尚不足以确认是哪一端缺陷。

先前通过通用插件管理查询得到的“computer-use is not installed”不能证明本地 bundled Computer Use 不存在；本地运行时和技能确实可用。不要据此要求重装或扩大权限。

未修改审批代码、全局权限、session allowlist，也未采用其他驱动绕过拒绝。待比较成功 harness 的同类调用，再决定是否需重建工具会话或提交客户端故障证据。

官方参考：

- https://learn.chatgpt.com/docs/computer-use
- https://learn.chatgpt.com/docs/config-file/config-reference
