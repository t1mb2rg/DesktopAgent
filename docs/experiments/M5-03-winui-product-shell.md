# M5-03 WinUI Product Shell

## 目标

在 M5-00 已通过的 WinUI 3 生命周期能力和 M5-02 Pending Queue 合同之上，把 feasibility shell 升级成第一版产品导航壳。

M5-03 只建立用户可理解的产品入口，不打开 Writer 或 unattended automation。

## 导航结构

- 概览
- 待确认
- 布局
- 设置

布局与设置当前是明确标注的安全占位，分别由 M5-05 与 M5-04 继续实现。

## Pending Queue

M5-03 使用共享产品数据库路径 `%LocalAppData%\DesktopAgent\desktop-agent.db`，通过 `SqliteDesktopPendingActionStore` 只读展示 Pending Queue。

当前行为：

- 首次进入待确认页时初始化共享 SQLite Schema；
- oldest-first 读取最多 100 项；
- 支持手动刷新；
- 空队列显示 Empty State；
- 数据库读取异常转换为页面内 Error InfoBar，不让异常直接冲出 UI 事件；
- 展示模型不是执行模型，不产生 WriterCommand。

M5-03 不在 UI 暴露 Confirm / Remember 按钮。M5-02 已经提供语义协调器，但当前产品壳还没有完整 fresh execution pipeline；如果现在把“确认”做成按钮，会出现“用户确认后 Queue 消失但桌面没有执行”的半截行为。

## 生命周期继承

必须保持 M5-00 Physical Gate 已验证的：

- 单实例；
- 第二次启动恢复现有窗口；
- × 只隐藏；
- 托盘恢复；
- 显式退出清除托盘并结束进程。

## 安全边界

- 不调用 Writer；
- 不生成物理坐标；
- 不打开 production unattended gate；
- 不修改 Recovery safety semantics；
- 布局页不进入 Layout Editing；
- Settings 只展示 `Assisted` 默认合同，不持久化修改。

## Physical Windows Gate

CI 通过后必须在真实 Windows 11 x64 客户端验证：

1. 正常启动进入产品壳，无 XAML runtime crash；
2. 概览 / 待确认 / 布局 / 设置四个入口均可切换；
3. Pending Queue 空状态或现有数据可正常读取，刷新不会崩溃；
4. 第二次启动仍为单实例并恢复窗口；
5. 隐藏到托盘 / × 隐藏 / 托盘恢复保持有效；
6. 显式退出移除托盘并终止进程；
7. 全程没有 Writer / unattended desktop movement。

只有 Physical Gate PASS 后，M5-03 才 COMPLETE。
