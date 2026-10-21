# 贡献指南

Tessel 是一个**会在真实桌面上动手**的程序：它会把你的图标挪到别的分区、跨屏搬动、必要时再搬回去。
所以这个仓库对"改动"的要求比一般 UI 项目更严一点 —— 不是为了流程好看，是因为**算错一个像素，
用户看到的就是图标整体错位**。

先说清楚现状：这个项目目前以单人维护为主，没有固定的响应时限。**发现问题、给出证据，比急着提 PR 更有用。**

---

## 一、动手之前先读三份

| 文档 | 为什么必须先读 |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | 五层依赖、一次写入的完整数据流、六道安全闸。改错层会在很远的地方炸 |
| [docs/pitfalls.md](docs/pitfalls.md) | 15 个真机踩过的坑。**你打算改的地方，大概率有人已经在这里栽过** |
| [docs/grid-measurement.md](docs/grid-measurement.md) | 桌面网格为什么"原点只能测"。要碰坐标相关代码，这篇是前提 |

想改什么、但不确定方向对不对，先开一个 issue 讨论 —— 比写完再被拒省时间。

---

## 二、环境

| 项 | 要求 |
| --- | --- |
| 操作系统 | Windows 11（真机验证必须；单测可在 Windows 上跑） |
| SDK | .NET 10（`global.json` 锁 `10.0.100`，`rollForward: latestFeature`） |
| 架构 | **x64**。项目只发布 x64 |
| 编辑器 | Visual Studio 2022 17.12+ / Rider 2024.3+ / VS Code + C# Dev Kit |

---

## 三、三分钟跑起来

```powershell
# 1. 构建（Debug 必须是 x64）
dotnet build DesktopAgent.sln -c Debug -p:Platform=x64

# 2. 跑全量测试（当前基线 2587 个用例）
dotnet test tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Release

# 3. 启动产品壳
.\src\DesktopAgent.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\DesktopAgent.WinUI.exe
```

### 两个必踩的坑

**1. 必须 `-c Debug -p:Platform=x64`**

`Directory.Build.props` 用编译符号决定渠道：`Debug` → **Dev 渠道**（数据在 `%LOCALAPPDATA%\Tessel.Dev`），
其它 → **Stable 渠道**（数据在 `%LOCALAPPDATA%\Tessel`）。用错配置不是"编译不过"，
而是**静默切到另一套数据**，你会以为数据丢了。

**2. 产品壳在跑时不能重新构建它**

Windows 会锁住 `DesktopAgent.WinUI.exe` 和同目录 DLL，构建报 `MSB3027/MSB3021`。
先关掉再 build：

```powershell
Get-Process DesktopAgent.WinUI -ErrorAction SilentlyContinue | Stop-Process
```

---

## 四、代码地图

```text
src/
  DesktopAgent.Core/           20.5k 行  领域模型、规则、布局计划、网格投影 —— 无 IO、无 UI
  DesktopAgent.Application/    15.4k 行  用例编排、策略与安全门、AI 契约
  DesktopAgent.WindowsInterop/ 26.9k 行  Win32 / COM：桌面读写、物理探测、线框、托盘
  DesktopAgent.Persistence/     7.1k 行  SQLite（schema v13）与 JSON 存储
  DesktopAgent.WinUI/          27.2k 行  WinUI 3 产品壳（导航、设计库、AI 助手、设置）
  DesktopAgent.Poc/            12.9k 行  只读诊断台 + 自动化引擎（实验用，不随产品发布）
tests/
  DesktopAgent.Tests/          55.7k 行  256 个文件、2587 个用例
```

**依赖方向是单向的**：`WinUI → Application → Core`，`WindowsInterop`/`Persistence` 实现 Core 定义的接口。
不要在 `Core` 里引用 `WindowsInterop`，也不要把 Win32 调用写进 `Application`。

---

## 五、提交规范

从现有历史归纳出来的格式，一条提交做一件事：

```text
type: 小写英文祈使句描述
```

| type | 用途 |
| --- | --- |
| `feat` | 新能力 |
| `fix` | 修缺陷 |
| `test` | 只加/改测试 |
| `docs` | 只改文档 |
| `build` | 构建、打包、依赖 |
| `ci` | 工作流 |
| `refactor` / `perf` / `chore` | 其它 |

示例（真实历史）：

```text
fix: rebuild live desktop overlays when the appearance preference changes
feat: Enter-to-send composer, unified card styling, and design library delete
docs: explain how desktop grid detection works and why the origin must be measured
ci: build and test without an explicit Platform in the release workflow
```

**别把无关改动塞进同一条提交**。这个仓库的 diff 经常一次几百行，评审靠的是"一条提交一个意图"。

---

## 六、红线：这些改动会被直接打回

### 6.1 安全原则不可放松

完整 11 条在 [docs/development.md](docs/development.md#已冻结的安全原则)。最容易踩的四条：

| 红线 | 为什么 |
| --- | --- |
| 不可实测的屏 → **fail closed**，不许"多数屏一致就算数" | 多屏相位本来就不同，投票会算错 |
| `Inferred` 证据 **永不能授权写入** | 推断只能参与预览。用数值转换把 `Inferred(1)` 变成 `Measured(1)` 是典型 fail-open |
| 证据必须绑定**拓扑指纹** | 换分辨率后旧事实还在用 = 静默错位 |
| 每次写入前必须有**安全检查点** | 它是"一键撤销"的唯一依据 |

### 6.2 不许"挪门柱"

验收预算（`MaximumAcceptanceMoveLimit = 10`）、判前一致性检查、凭据一次性消费 ——
这些数字和检查是**刻意**的。撞上限制时的正确反应是"让候选更小 / 让提示更清楚"，
不是"把限制调大"。

### 6.3 不要在系统消息栈里做重活

`WndProc` 里只允许投递事件，随后 `DispatcherQueue.TryEnqueue` 异步处理。
在 `WM_DISPLAYCHANGE` 里同步读桌面或弹 UI，会拖死 Explorer。

### 6.4 枚举不要走数值转换

`ExplorerGridEvidenceSource { Measured = 0, Inferred = 1 }` 这类枚举，**必须显式 switch**。
曾经因为一次强制转换把推断当成实测，直接绕过了整条安全链（见 pitfalls A2）。

---

## 七、测试要求

| 要求 | 说明 |
| --- | --- |
| 改动必须带测试 | 修 bug 补回归测试；加能力补契约测试 |
| 全量必须绿 | `2587/2587`，PR 会跑 CI；本地先跑一遍 |
| **纯逻辑**才适合单测 | 涉及真实 Explorer 行为的，必须上真机 |
| 不要 mock 掉被测对象 | 这个仓库的测试大量使用真实 SQLite（临时文件），而不是内存替身 |

### 单测覆盖不到的地方

桌面读写、网格探测、线框覆盖、应用/撤销这条链 —— **单测证明不了它们对**。
这类改动请在 PR 描述里附：真机截图、`grid-probe-*.txt` 日志片段、或前后坐标对比。

---

## 八、真机验证怎么做

仓库里有一套 UIA 自动化脚本（开发自用，不是产品的一部分），位于 `tools/uia-acceptance/`：

| 脚本 | 用途 |
| --- | --- |
| `Act-Ui.ps1` | 按 `-AutomationId` / 名称读、点、填、展开、读日志尾巴 |
| `Win-Windows.ps1` | 原生窗口：枚举、按 `WM_COMMAND` 点按钮、发按键、前台化 |
| `Probe-Button.ps1` | 打印元素类型/可用性/矩形/支持的 Pattern，排查"为什么点不到" |

几条实测经验：

- Win11 的**托盘图标用 UIA 枚举不到**（`Shell_TrayWnd` 子树里 Button 数为 0，是 XAML 岛）；
- 元素 `IsOffscreen` 时鼠标点不到，但 **UIA Invoke 仍然有效**；
- 截图要用 `PrintWindow(hwnd, hdc, 2)`，`CopyFromScreen` 会抓到遮挡窗口；
- 多屏 + DPI 下 UIA 报的 rect 可能不可靠，**键盘导航比坐标点击稳**。

---

## 九、提 PR

1. 从 `main` 切分支：`feat/xxx`、`fix/xxx`；
2. 一条提交一个意图，提交信息按上面的格式；
3. PR 描述里写清：**改了什么 / 为什么 / 怎么验证的**（真机证据优先于"测试通过"）；
4. 等 CI：`build` 必须绿；只有 `release` 由 tag 触发，PR 不跑；
5. 涉及上述红线的改动，请在 PR 里**单独一节**说明为什么没有放松安全边界。

---

## 十、报 Bug 需要的信息

没有这些基本没法定位：

| 信息 | 从哪拿 |
| --- | --- |
| 版本 | 设置页或 Release 页；窗口标题能看出渠道（`Tessel` = Stable，`Tessel Dev` = Dev） |
| 复现步骤 | 具体到点了哪个按钮、当时在哪个阶段（设备准备 / 编辑模板 / 预览与应用） |
| 崩溃日志 | `%TEMP%\tessel-product-crash.log`（如果程序异常退出） |
| 校准日志 | `%LOCALAPPDATA%\Tessel\logs\grid-probe-*.txt` |
| 显示器配置 | 每块屏的分辨率、缩放比例、排列方式、任务栏位置 |
| 截图 | 优先截**应用窗口本身**（`PrintWindow`），桌面整体截屏容易暴露隐私 |

**别在公开 issue 里贴 API Key**（它存在 Windows DPAPI Secret Store 里，不该出现在任何日志中）。

---

## 十一、最需要的贡献

按实际价值排：

1. **更多显示环境的实测数据**（P0-3）—— 单屏、双屏混合 DPI、竖屏主屏、侧边任务栏。
   哪怕只提供一份 `grid-probe-*.txt` + 配置描述。
2. **崩溃 `0xc000027b` 的复现路径**（P0-2）—— 如果你能稳定复现，价值极高。
3. **P1 里的前三条小修**（HttpClient 复用、Markdown 长度上限、`Single()` 异常）——
   改动小、边界清楚，适合第一次贡献。
4. **文档纠错** —— 文档里写的和代码不一致，就是 bug。

---

## 十二、许可

提交即表示同意以 [GPL-3.0-or-later](LICENSE) 授权。第三方组件声明见
`src/DesktopAgent.WinUI/ThirdPartyNotices.txt`。
