# DesktopAgent

DesktopAgent 是一个面向 Windows 11 的本地桌面管理 Agent。它计划通过规则和 AI 理解文件的项目用途，在不接管 Explorer 原生桌面的前提下，为活跃工作文件生成静态分区、提供分类与布局建议，并在用户确认后完成受控整理与恢复。

> 当前状态：**M1 安全桌面读写与恢复技术验证已完成，准备进入 M2 分区与布局规则阶段。**  
> 详细结论：[`docs/milestones/M1-closeout.md`](docs/milestones/M1-closeout.md)

## 已确定的技术路线

- .NET 10 LTS
- WPF 首版界面，架构允许后续增加 WinUI 3 表现层
- Shell COM / 必要的 Win32 API 集中封装在 WindowsInterop
- 核心业务逻辑不依赖 WPF
- SQLite 本地数据
- 用户自带 OpenAI 兼容 API Key（后续阶段接入）
- GPL-3.0-or-later

## M1 已完成能力

- 枚举任意数量显示器，读取主屏、工作区、DPI、缩放和虚拟桌面物理像素坐标；
- 支持位于主屏左侧或上方显示器的负坐标；
- 通过 Windows Shell COM 只读获取桌面图标、StableId、类型、位置和显示器归属；
- 将完整桌面布局保存到 SQLite，并在应用重启后读取历史快照；
- 检测自动排列状态和当前图标间距；
- 使用当前 PIDL 和 Ordinal StableId 唯一匹配执行单图标定位；
- 生成指定显示器恢复计划，分析静态阻断、依赖和循环；
- 将恢复计划转换为确定性的直接移动与临时中转步骤；
- 在真实写入前重新读取、重新规划、保存并回读验证安全快照；
- 记录 RecoverySession、Forward / Rollback Attempts 和结构化失败状态；
- 在严格 ScopeGate 下完成单图标、单步骤、无中转的真实布局恢复；
- 通过独立重新读取确认最终位置。

最终自动化验证：**325/325 tests passed**，Release Build 为 0 Warning / 0 Error，GitHub Actions 通过。

## 当前 PoC

WPF PoC 提供：

1. 显示器拓扑读取和目标显示器选择；
2. 桌面图标完整读取和显示器归属展示；
3. 手动保存布局快照及查看历史快照；
4. M1-04B 单图标相邻移动与同会话恢复；
5. M1-05C-B 指定显示器恢复预览；
6. 单图标、单步骤、无中转的受控真实恢复；
7. 安全快照、执行会话、实际位置和异常诊断展示。

PoC 不是正式产品 UI，不后台常驻，也不会自动执行桌面写入。

## 项目结构

```text
DesktopAgent/
├─ src/
│  ├─ DesktopAgent.Core/
│  ├─ DesktopAgent.WindowsInterop/
│  ├─ DesktopAgent.Persistence/
│  └─ DesktopAgent.Poc/
├─ tests/
│  └─ DesktopAgent.Tests/
├─ docs/
│  ├─ decisions/
│  ├─ experiments/
│  └─ milestones/
└─ .github/workflows/
```

## 本地运行

要求：

- Windows 11
- Visual Studio 2026 或支持 .NET 10 的 Visual Studio
- `.NET 桌面开发` 工作负载
- .NET 10 SDK

```powershell
dotnet restore DesktopAgent.sln
dotnet build DesktopAgent.sln --configuration Release
dotnet test tests/DesktopAgent.Tests/DesktopAgent.Tests.csproj `
  --configuration Release `
  --no-build
dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj `
  --configuration Release
```

## M1 验收结果

M1 的最终技术验证闭环已经完成：

```text
显示器拓扑
→ 桌面图标读取
→ 布局快照
→ 安全移动后端
→ 恢复计划
→ 执行步骤计划
→ 安全执行协调器
→ 受控真实恢复
→ 独立重新读取
```

原始规划中的可视分区层没有在 M1 实现。M1 范围在开发过程中明确收敛为 Shell 安全读写与恢复底座；分区模板、分区 UI 和布局规则顺延到 M2。

## M2 下一阶段

M2 建议依次推进：

1. 分区和模板领域模型；
2. 确定性分区分配与网格布局规划器；
3. 布局预览、差异解释和用户固定/排除规则；
4. 复用 M1 Coordinator 的受控多图标执行；
5. 在规则系统稳定后再接入 AI 分类建议。

M2 初期仍不进行无确认后台写入、文件归档或 AI 直接调用 Writer。

## 安全原则

- AI 和 UI 不直接调用 Shell Writer 或文件系统；
- 新布局策略只能生成正式计划，不能直接执行；
- 桌面读取不完整时禁止保存或恢复；
- StableId 和 DeviceName 使用 Ordinal 精确匹配；
- 自动排列开启时拒绝移动；
- 真实执行前必须重新读取和重新规划；
- 每次写入前必须保存并回读验证完整安全快照；
- Writer 异常视为可能已经产生副作用；
- 身份或前置位置不可信时禁止盲目回滚；
- 默认不永久删除文件；
- 宁可停止整理，也不冒险继续执行。

## 当前限制

- 多步骤、循环和临时中转已通过自动化测试，但尚未进行真实桌面执行；
- RecoverySession 尚未跨进程持久化；
- 未监听显示器热插拔；
- `\\.\DISPLAYx` 不作为长期稳定硬件身份；
- 未覆盖所有 Windows 11 / Explorer 版本和极端混合 DPI 场景；
- 未实现正式分区层、自动整理、文件归档、托盘程序或安装包。
