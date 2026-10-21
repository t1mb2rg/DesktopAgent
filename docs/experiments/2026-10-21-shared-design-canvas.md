# 两个页面共享设计画布（2026-10-21）

## 行为

- 设计库顶部“当前草稿”打开可拖动的共享草稿画布。
- 已保存版本的“编辑此设计”经明确确认载入共享草稿后，留在设计库编辑，不再强制跳 AI 助手。
- AI 助手与设计库均使用 WorkspaceDesignCanvas，复用相同绘制、实测网格吸附、跨屏拖动、缩放、标题避让和修订检查。
- 两个控制器引用主窗口持有的 ProductWorkspaceSession，没有复制设计数据。已有保存队列继续自动持久化草稿；修改草稿不自动创建正式版本。
- 设计库提供共享撤销/重做、“保存为新版本”和“检查精确预览”。保存使用现有 DesktopWorkspaceDraftProductService；精确预览仍需新鲜观察、独立检查、显式应用。
- 共享会话修订变化继续触发 MainWindow 原有预览失效流程。预览检查期间草稿变化，结果不会覆盖新设计。
- 页面卸载、阶段切换、尺寸变化、指针丢失取消拖动；另一处并发修改取消旧拖动。草稿保存错误展示在画布摘要中。

## 验证

- PackagingTest x64 Debug 构建成功（11 个既有 warnings，0 errors）。
- 已有测试 2582 passed / 0 failed / 0 skipped。日志和 TRX：artifacts/release-baseline/shared-design-canvas-*。
- 本次未进行真实窗口拖动和跨页面视觉验收；未调用 Writer 移动图标。
- 试用程序：artifacts/shared-design-canvas/DesktopAgent.WinUI.exe。
