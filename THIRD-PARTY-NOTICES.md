# 第三方组件与许可证

Tessel 的**源代码**以 GNU General Public License v3.0 or later 发布，见 [LICENSE](LICENSE)。

发布包（`Tessel-*-win-x64.zip` 与 `Tessel-Setup-*.exe`）里除了 Tessel 自己的代码，还包含下表列出的第三方组件。
这些组件**不适用 GPL**，各自按其自身许可分发；它们与 Tessel 构成 GPL-3.0 第 5 条意义上的"聚合体"（aggregate），
不是衍生作品。因此：

- 想修改、重新编译或再分发 **Tessel 的源代码**，遵守 GPL-3.0-or-later。
- 想再分发 **打包好的安装程序**，除 GPL 外还必须一并遵守下表各组件的条款（尤其是 Microsoft 的组件）。

---

## 一、随发布包分发的组件

### Microsoft Windows App SDK（专有 EULA，非开源许可）

| 项 | 值 |
| --- | --- |
| 组件 | `Microsoft.WindowsAppSDK` 2.3.1 及其运行时（`Microsoft.WindowsAppRuntime.dll`、`Microsoft.ui.xaml.dll`、`Microsoft.WinUI.dll`、`WinUIEdit.dll`、`DWriteCore.dll`、`dcompi.dll`、`dwmcorei.dll`、`Microsoft.Windows.Widgets.dll` 等） |
| 许可 | **MICROSOFT SOFTWARE LICENSE TERMS — MICROSOFT WINDOWS APP SDK**（专有许可，含争议解决与集体诉讼弃权条款） |
| 许可全文 | 随 NuGet 包提供：`microsoft.windowsappsdk/2.3.1/license.txt`；亦见 <https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.3.1> |
| 说明 | 该许可允许把运行时随应用一起再分发，但要求保留许可声明且不得拆分使用。**它不是 MIT。** |

> 这是本项目唯一非开源的运行时依赖。它决定了发布包不能被描述为"完全自由的软件"——
> 源码是自由的，运行的 UI 框架不是。

### .NET 10 运行时（自包含发布）

| 项 | 值 |
| --- | --- |
| 组件 | `System.Private.CoreLib.dll`、`coreclr.dll`、`System.Private.Xml.dll`、`System.Linq.Expressions.dll`、`System.Security.Cryptography.dll` 等全部 `System.*` / `Microsoft.*` 运行时程序集 |
| 许可 | MIT |
| 版权 | Copyright (c) .NET Foundation and Contributors |
| 来源 | <https://github.com/dotnet/runtime> · <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> |

### Microsoft.Windows.SDK.NET.dll（CsWinRT 生成的 Windows SDK 投影）

| 项 | 值 |
| --- | --- |
| 许可 | MIT |
| 版权 | Copyright (c) Microsoft Corporation |
| 来源 | 由 `Microsoft.Windows.CsWin32` / CsWinRT 工具链生成 · <https://github.com/microsoft/CsWinRT> |

### Markdig（Markdown 渲染）

| 项 | 值 |
| --- | --- |
| 版本 | 1.3.2 |
| 许可 | BSD-2-Clause |
| 版权 | Copyright (c) 2018-2019, Alexandre Mutel |
| 来源 | <https://github.com/xoofx/markdig> |
| 全文 | 见 [`src/DesktopAgent.WinUI/ThirdPartyNotices.txt`](src/DesktopAgent.WinUI/ThirdPartyNotices.txt)，并随发布包一起分发 |

### Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite

| 组件 | 版本 | 许可 | 版权 |
| --- | --- | --- | --- |
| `Microsoft.Data.Sqlite` | 10.0.10 | MIT | Copyright (c) .NET Foundation and Contributors |
| `Microsoft.Data.Sqlite.Core` | 10.0.10 | MIT | 同上 |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.12 | Apache-2.0 | Copyright (c) Eric Sink / SQLitePCLRaw contributors |
| `SQLitePCLRaw.core` | 2.1.12 | Apache-2.0 | 同上 |
| `SQLitePCLRaw.provider.e_sqlite3` | 2.1.12 | Apache-2.0 | 同上 |
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.12 | Apache-2.0（对 SQLite 本身的封装） | 同上 |
| SQLite 本体（`e_sqlite3.dll`） | 3.x | **Public Domain** | SQLite 作者声明放弃版权 · <https://www.sqlite.org/copyright.html> |

来源：<https://learn.microsoft.com/dotnet/standard/data/sqlite/> · <https://github.com/ericsink/SQLitePCL.raw>

### Microsoft.Extensions.*（仅 PoC 诊断工具使用，不随产品发布）

| 组件 | 版本 | 许可 |
| --- | --- | --- |
| `Microsoft.Extensions.Hosting` | 10.0.0 | MIT |
| `Microsoft.Extensions.Logging.Debug` | 10.0.0 | MIT |

版权：Copyright (c) .NET Foundation and Contributors · <https://github.com/dotnet/runtime>

### System.Security.Cryptography.ProtectedData（DPAPI 封装）

| 项 | 值 |
| --- | --- |
| 版本 | 10.0.10 |
| 许可 | MIT |
| 版权 | Copyright (c) .NET Foundation and Contributors |

---

## 二、仅开发与测试使用（不随发布包分发）

| 组件 | 版本 | 许可 | 说明 |
| --- | --- | --- | --- |
| `Microsoft.Windows.CsWin32` | 0.3.106 | MIT | 编译期生成 Win32 P/Invoke 声明 |
| `xunit` | 2.9.3 | Apache-2.0 | 测试框架 · <https://xunit.net> |
| `xunit.runner.visualstudio` | 2.8.2 | Apache-2.0 | 测试运行器 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | 测试宿主 |

这些只在 `dotnet test` 时存在，不进入任何发布产物。

---

## 三、构建与打包工具（不随发布包分发）

| 工具 | 许可 | 用途 |
| --- | --- | --- |
| .NET SDK 10 | MIT | 编译 |
| [Inno Setup](https://jrsoftware.org/isinfo.php) 6 | Inno Setup License（允许自由使用与再分发安装程序，但不得再分发编译器本体） | 生成 `Tessel-Setup-*.exe` |
| Inno Setup 简体中文语言文件 | 随 Inno Setup 项目同许可 | 安装界面本地化 |

安装器里嵌入的简体中文翻译来自 Inno Setup 官方仓库 <https://github.com/jrsoftware/issrc>。

---

## 四、本项目自己的图标

`src/DesktopAgent.WinUI/Assets/tessel.ico` 与 `tessel-256.png` 由项目作者提供，随源代码一起按 GPL-3.0-or-later 授权。

---

## 五、如何更新本文件

新增或升级依赖后，用本地 NuGet 缓存里的 `.nuspec` 核对 `license` 字段，不要凭印象填写：

```powershell
$x = [xml](Get-Content "$env:USERPROFILE\.nuget\packages\<id>\<version>\<id>.nuspec")
$x.package.metadata.license
```

如果某次升级引入了新的专有依赖，要同时更新第一节和 README 的"已知限制"。

---

## 六、报告许可问题

发现遗漏或错误，请开 issue 说明具体组件与依据。
