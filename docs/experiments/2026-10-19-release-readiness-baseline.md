# 接手与发布验证基线（2026-10-19）

## 验证对象

- 分支：`agent/grid-measurement-fact-v1`
- 提交：`be4c3a48b15ab24d903b1b0a86502116d78dcaba`
- 本地 SDK：.NET 10.0.302（由 global.json 的 latestFeature 策略选择）。
- 开始时业务源码无未提交变更；存在未跟踪的 `.serena/` 工具目录。

## 本轮实际执行

| 检查 | 结果 |
| --- | --- |
| `dotnet build DesktopAgent.sln -c Release --verbosity minimal`（含还原） | 通过，132 warnings / 0 errors |
| `dotnet build DesktopAgent.sln -c Debug --no-restore --verbosity minimal -flp:logfile=artifacts/release-baseline/debug-build.log` | 通过，132 warnings / 0 errors |
| `dotnet test tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Release --no-build --no-restore --results-directory artifacts\release-baseline --logger "trx;LogFileName=release-baseline.trx" --logger "console;verbosity=minimal"` | 2453 passed / 0 failed / 0 skipped |

测试结果：`artifacts/release-baseline/release-baseline.trx`。
Debug 构建日志：`artifacts/release-baseline/debug-build.log`。
上述 artifacts 被 gitignore 排除，仅保存在本机。

沙箱内 `dotnet --info` 出现 Windows Installer 类型初始化异常；沙箱外构建、还原和测试正常完成。本轮未修改 SDK、依赖版本或业务代码。不能将此前沙箱内测试的退出码 1 认定为业务测试失败。

132 条构建警告是本轮基线，尚未证明与 README 所述历史 121 条警告的差异来源。可见类别包括 nullable 类型不匹配、未使用事件或字段、WPF/WinForms DPI manifest 提示及 xUnit 分析器提示。

## 发布判断与后续顺序

当前只能确认本地 Debug / Release 可构建、Release 自动化测试通过，不能据此宣布发布就绪。本轮没有启动产品、移动桌面图标、执行恢复、安装升级或长时间驻留验收，也没有核实远端 CI 状态。

1. 优先调查反馈 F-3：收集冻结预览与 fresh observation 的逐项差异，确认重复拒绝的根因。源码已确认 UI 在进入 Apply service 前清除当前 PreviewId 的一次性入口；这不等于已证明它是桌面差异的根因。不得仅为重试方便放宽 fresh check 或恢复写入权限。
2. 处理 F-1 / F-2 / F-4 / F-5 的首次引导、布局选择、只读审阅与工作区复用体验，补充对应回归验证。
3. 在修复后的候选版本完成真实 Windows 产品流程、恢复与稳定性验收。旧版本的性能记录不能直接替代候选版本证据。
4. 核实并完成 Portable / 安装包、干净环境启动和升级保留数据验证。`docs/m5-08-build-channels.md` 当前仍将这条发布链列为计划。
5. 更新 README 的产品状态与验证入口，区分本地结果、远端 CI 和真实桌面验收。

关联反馈：`docs/suggestions/product-feedback-backlog.md`。
