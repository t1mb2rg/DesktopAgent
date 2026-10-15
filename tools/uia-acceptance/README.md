# 真机验收驱动脚本（UI Automation / Win32）

这三个脚本不是产品代码，而是把**真机验收**变成可重复动作的工具：驱动 WinUI 面板、读控件状态、
点确认框、查"按钮为什么点不动"。2026-10-15 在本机三屏真机上逐项用过（网格校准、面板三阶段、
应用到桌面、无人值守武装对话框）。

依赖：Windows + PowerShell 5.1+（`UIAutomationClient` 由 .NET Framework 提供）。

| 脚本 | 用途 |
| --- | --- |
| `Act-Ui.ps1` | UI Automation 驱动：列控件、读文本、`invoke` / `select` / `selectname` / `expand` / `check` / `settext`。`-ProcessName` 默认 `DesktopAgent.WinUI`；诊断工作台进程名是 `DesktopAgent.Diagnostics`。 |
| `Win-Windows.ps1` | Win32 层驱动：顶层窗口枚举、按 `WM_COMMAND` id 点按钮、按键（`-Action keys -KeySpec LWIN+B` 等）、`BM_CLICK`。用于 `#32770` 确认框与「Windows App SDK unavailable」提示。 |
| `Probe-Button.ps1` | 打印元素的 ControlType / enabled / offscreen / rect / 支持的 pattern，可选 `-Click` 鼠标点击。定位"按钮为什么点不动"时用。 |

## 常用手法

```powershell
# 列出面板控件及其 AutomationId
pwsh -File tools/uia-acceptance/Act-Ui.ps1 -Action list

# 点按钮：按 AutomationId，或按名称子串（中文写文件，别写进脚本正文）
pwsh -File tools/uia-acceptance/Act-Ui.ps1 -Action invoke -AutomationId PreviewLayoutButton
pwsh -File tools/uia-acceptance/Act-Ui.ps1 -Action invoke -TextFile use.txt

# 读一段可能很长的文本（默认只回末尾若干字符）
pwsh -File tools/uia-acceptance/Act-Ui.ps1 -Action read -AutomationId DiagnosticsText -TailChars 4000

# 点确认框上的「是(Y)」（IDYES = 6）
pwsh -File tools/uia-acceptance/Win-Windows.ps1 -Action click -Handle <hwnd> -CommandId 6
```

## 踩过的坑（都真的踩了）

- **脚本一律保持 ASCII**：中文写进 `.ps1` 会被 PowerShell 5.1 按 ANSI 读出乱码，导致编译失败或
  永远匹配不到；需要中文一律写进 UTF-8 文件再用 `-TextFile` 传。
- **UIA `Invoke` 能点屏幕外的元素**：面板元素 rect 可能落在 y≈1740，超出 2560×1440 主屏；
  此时鼠标点击不会命中，而 `InvokePattern` 照样生效。这是"点了没反应"的常见原因。
- **元素 disabled 时取 pattern 会失败**（报 `supports neither Invoke nor Expand`）：
  先用 `Probe-Button.ps1` 看 `enabled`，不要靠猜。
- **`-Action select` 需要 `-AutomationId`**（`-TextFile` 只用于 `invoke`）。
- **ComboBox 的项要先 `expand`** 才能 `read` / `select -Index`（虚拟化，未展开时子项不出现）。
- **`Start-Process` 不会把环境变量传给孙进程**：要给被测进程设环境变量（如
  `DESKTOPAGENT_DATA_DIR`），必须在 `.cmd` 里 `set` 之后再 `start`。
