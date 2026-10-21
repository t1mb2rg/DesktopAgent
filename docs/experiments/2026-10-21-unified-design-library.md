# 统一设计库入口（2026-10-21）

用户确认取消单屏模板的独立主入口，统一使用设计库。

- 左侧设计库使用完整高度，旧模板归入默认折叠的“历史布局”；没有历史资产时隐藏该区域。
- 旧模板及其规则继续使用原存储和兼容编辑路径，不删除、不静默转换数据。
- “新建设计”进入现有 AI/画布工作台，保留当前草稿，避免导航动作清空用户工作。
- 保存版本详情新增“编辑此设计”：明确确认后通过共享会话的 ReplaceWorkspaceDraft 载入选中版本，使用修订号检查避免覆盖并发修改，可撤销。原版本只读，实际桌面不移动。
- 阶段标签统一为“设计库”，精确预览、确认应用、恢复路径不变。

验证：PackagingTest x64 Debug 构建成功，0 errors（11 个既有 warnings）；已有测试全部通过，详情见 artifacts/release-baseline/unified-design-library-tests.log 和同名 TRX。git diff --check 通过。未进行本次界面的真实窗口点击验收。

试用程序：artifacts/unified-design-library/DesktopAgent.WinUI.exe。
