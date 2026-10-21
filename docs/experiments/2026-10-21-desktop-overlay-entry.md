# 恢复桌面线框入口（2026-10-21）

简化阶段导航时误将 OverlayToggleButton 留在已隐藏的 LayoutStageBar 内。本次将按钮和显示状态移到独立常驻 DesktopOverlayToolbar，浏览设计、共享草稿及精确预览时均可访问。

只读线框优先显示当前可见的共享草稿或设计库选中版本；已有精确预览与历史模板路径保留。草稿线框只需要新鲜观察与有效实测网格，不调用模型、不生成执行权限、不移动图标。再次点击隐藏。

回归契约从旧的“按钮位于阶段栏内”更新为更严格的“唯一按钮位于独立工具栏且没有任何 Collapsed 祖先”，避免同类入口被误藏。

PackagingTest x64 Debug 构建成功；2584 tests passed / 0 failed / 0 skipped。日志见 artifacts/release-baseline/desktop-overlay-entry-*。本次未进行新版按钮的真实窗口点击验收。

程序：artifacts/desktop-overlay-entry/DesktopAgent.WinUI.exe。
