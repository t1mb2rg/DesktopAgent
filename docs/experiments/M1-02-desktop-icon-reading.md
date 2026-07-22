# M1-02：只读获取 Windows 桌面图标列表及其物理像素坐标

## 目标

通过 Windows Shell 公开 COM 接口，只读获取 Explorer 桌面视图中的图标项目，记录其显示名称、标识、类型和虚拟桌面物理像素坐标。

## 使用的 Shell COM 调用链

### 主路径（FindWindowSW + SWC_DESKTOP）

```
1. SHGetKnownFolderIDList(FOLDERID_Desktop) / SHGetSpecialFolderLocation(CSIDL_DESKTOP)
   → 获取桌面 PIDL（调用者 ILFree 释放）

2. IShellWindows (CLSID: {9BA05972-F6A8-11CF-A442-00A0C90A8F39})
   └─ FindWindowSW(pvarLoc=桌面PIDL字节数组, SWC_DESKTOP=8, SWFO_NEEDDISPATCH=1)
      → 返回桌面窗口的 IDispatch
      → 调用方式：Type.InvokeMember("FindWindowSW", …) — 迟绑定 IDispatch
        （不使用 managed 接口 + [DispId] 属性，见下方"IShellWindows 互操作策略"）

3. IDispatch → QI → IServiceProvider (IID: {6D5140C1-7436-11CE-8034-00AA006009FA})
   └─ QueryService(SID_STopLevelBrowser={4C96BE40-…}, IID_IShellBrowser)
      → IShellBrowser

4. IShellBrowser
   └─ QueryActiveShellView() → IShellView
      └─ GetItemObject(SVGIO_BACKGROUND, IID_IFolderView2) → IFolderView2
         ├─ ItemCount(SVGIO_ALLVIEW) → 图标总数
         ├─ GetItem(i) → PIDL（调用者 CoTaskMemFree 释放）
         ├─ GetItemPosition(pidl) → (X, Y) 物理像素坐标
         └─ GetFolder(IID_IShellFolder) → IShellFolder（桌面文件夹）
            ├─ GetDisplayNameOf(pidl, SHGDN_NORMAL) → 显示名称
            ├─ GetDisplayNameOf(pidl, SHGDN_FORPARSING) → 解析名称
            └─ GetAttributesOf(pidl) → SFGAO 属性位
```

### 降级路径（Item 枚举 + Progman 类名）

仅当主路径 FindWindowSW 在任何环节失败时启用。同样使用 `ShellWindowsLateBound` 迟绑定调用 `Count` 和 `Item`：

```
ShellWindowsLateBound.GetCount() → Type.InvokeMember("Count", BindingFlags.GetProperty, …)
ShellWindowsLateBound.GetItem(i) → Type.InvokeMember("Item", BindingFlags.InvokeMethod, …)
  → IDispatch → QI → IShellBrowser
    └─ GetWindow() → HWND → GetClassName() == "Progman"  (桌面识别)
    └─ 后续与主路径 4 相同
```

降级路径不依赖 SWC_DESKTOP 或 IServiceProvider，但依赖窗口类名 "Progman"。

### FindWindowSW 关键参数说明

| 参数 | 值 | 说明 |
|------|-----|------|
| `pvarLoc` | `byte[]`（PIDL 的 ITEMIDLIST 字节副本） | 桌面 PIDL 作为 `VT_ARRAY\|VT_UI1` 传入 |
| `pvarLocRoot` | `Type.Missing`（VT_EMPTY） | 不使用根范围限定 |
| `swClass` | `8`（SWC_DESKTOP） | 仅查找桌面类别的 Shell 窗口 |
| `swfwOptions` | `1`（SWFO_NEEDDISPATCH） | 若无现有窗口则启动 |
| 返回值 | `IDispatch` | 桌面 Shell 窗口的自动化对象 |

### IShellWindows 互操作策略：迟绑定 IDispatch

**问题：** 最初尝试使用 managed 接口 `[ComImport, InterfaceType(InterfaceIsIDispatch)]` + `[DispId(8)]` 定义 FindWindowSW。实机测试返回 `DISP_E_MEMBERNOTFOUND (0x80020003)`。

**根因：** `8` 是 `SWC_DESKTOP` 常量值，不是 `FindWindowSW` 在 SHDocVw 类型库中的自动化 DISPID。SHDocVw 类型库中 IShellWindows 为 dual 接口，MEMBERID 使用 `0x60020000` 偏移（`FindWindowSW` 实际 DISPID 取决于类型库版本，可能为 `0x60020008` 或运行时由 MIDL 自动分配）。

**不再猜测 DISPID。** 改为使用 `ShellWindowsLateBound` 封装：

- 通过 `Activator.CreateInstance` 创建 COM 对象后获取 `__ComObject` 的 `Type`；
- 所有调用（`FindWindowSW`、`Count`、`Item`）使用 `Type.InvokeMember` 方法名字符串；
- COM 运行时调用 `IDispatch.GetIDsOfNames("FindWindowSW")` 从已注册的 SHDocVw 类型库解析正确的 DISPID；
- 然后调用 `IDispatch.Invoke(DISPID, DISPATCH_METHOD, ...)`；
- `ref`/`out` 参数通过 `ParameterModifier` 标记。

**优势：** DISPID 由操作系统从已注册类型库解析，永远正确；不依赖硬编码常量。

### PIDL → VARIANT 传递方式

桌面 PIDL 通过 `GetDesktopPidl()` 获取（优先 `SHGetKnownFolderIDList`，回退 `SHGetSpecialFolderLocation`），复制为 `byte[]`（`PidlToByteArray`），再通过 `[MarshalAs(UnmanagedType.Struct)] ref object` 传递到 IDispatch 方法调用中，.NET 运行时将其 marshaling 为 `VARIANT(VT_ARRAY | VT_UI1)`。

### 实际使用的桌面 PIDL API

`GetDesktopPidl()` 的实际路径：

```
1. SHGetKnownFolderIDList(FOLDERID_Desktop = {B4BFCC3A-DB2C-424C-B029-7FE99A87C641}, dwFlags=0, hToken=null)
   → 现代 API，Windows Vista+。返回桌面 PIDL，调用者 ILFree 释放。

2. [仅当步骤 1 失败时] SHGetSpecialFolderLocation(hwnd=null, CSIDL_DESKTOP=0x0000)
   → 已弃用 API，Windows 95+。作为回退路径保留。
```

- `SHGetSpecialFolderLocation` 和 `CSIDL_DESKTOP` 声明仅作为 `SHGetKnownFolderIDList` 的回退；
- `SHGetDesktopFolder`、`SHGetPathFromIDListW`、`SHGetNameFromIDList`、`SHCreateItemFromIDList` 声明在当前实现中未使用，保留为备用。
- 所有 PIDL 通过 `ILFree` 释放，不使用 `CoTaskMemFree` 或 `Marshal.FreeCoTaskMem` 释放 `GetDesktopPidl` 返回的 PIDL。

### 连接方式诊断

读取结果通过 `DesktopIconReadResult` 携带连接方式：

```
DesktopIconReadResult
├─ Icons: IReadOnlyList<DesktopIconDescriptor>  — 已分配显示器归属的图标列表
├─ FailedItemCount: int                         — 单项读取失败数
├─ ConnectionMethod: enum
│   ├─ FindWindowSW        — 主路径成功
│   └─ EnumerationFallback — 降级路径（主路径失败）
├─ ReadTime: TimeSpan                          — 读取耗时
└─ PrimaryPathException: Exception?            — 主路径失败原因（降级时非 null）
```

PoC 状态栏显示格式示例：
- 主路径成功：`读取完成：共 15 个桌面图标。连接方式：FindWindowSW。`
- 降级路径：`读取完成：共 15 个桌面图标。连接方式：EnumFallback。`
  - 图标统计栏同时显示：`连接：EnumFallback | 主路径失败：FindWindowSW 返回的对象不支持 IServiceProvider。`

当主路径和降级路径都失败时，抛出 `AggregateException` 同时包含两条路径的异常信息。

### 辅助 Shell32 API

- `StrRetToBuf` — 将 STRRET 转为字符串缓冲区
- `ILFree` — 释放由 `SHGetKnownFolderIDList` / `SHGetSpecialFolderLocation` 返回的 PIDL
- `CoTaskMemFree` — 释放由 `IFolderView2.GetItem` 返回的 PIDL

### 为什么没有使用 SysListView32 / 远程内存

| 方案 | 风险 |
|------|------|
| SysListView32 LVM_GETITEMPOSITION | Explorer 内部实现细节，Windows 版本间可能变化 |
| 跨进程 ReadProcessMemory | 安全软件可能拦截；需要 PROCESS_VM_READ 权限 |
| UI Automation | 对桌面图标支持有限；性能差；返回坐标语义不明确 |
| 自行遍历 %Desktop% 文件夹 | 无法获取图标位置；OneDrive 重定向支持差 |

当前方案使用 **公开且文档化的 Shell COM 接口**：
- `FindWindowSW + SWC_DESKTOP` 是微软文档中推荐的桌面窗口查找方式；
- `IServiceProvider + SID_STopLevelBrowser` 是获取顶层 ShellBrowser 的标准 COM 模式；
- `IShellView` / `IFolderView2` 均来自 Windows SDK；
- 坐标通过 `IFolderView2.GetItemPosition` 获取，这是 Explorer 用于排列图标的同一接口；
- 完全只读，不修改任何状态。

## COM 和 PIDL 的释放策略

| 资源 | 分配来源 | 释放方式 |
|------|---------|---------|
| IShellWindows | `Activator.CreateInstance` | `SafeReleaseComObject` (Marshal.ReleaseComObject) |
| IDispatch (from FindWindowSW) | Shell COM | `SafeReleaseComObject` |
| IServiceProvider | QI from IDispatch | `SafeReleaseComObject` |
| IShellBrowser | `QueryService` (主) 或 QI (降级) | `SafeReleaseComObject` |
| IShellView | `QueryActiveShellView` | `SafeReleaseComObject` |
| IFolderView2 | `GetItemObject` | `SafeReleaseComObject` |
| IShellFolder | `GetFolder` | `SafeReleaseComObject` |
| PIDL (from GetItem) | Shell COM | `Marshal.FreeCoTaskMem` |
| PIDL (from SHGetKnownFolderIDList) | Shell32 | `ILFree` |
| STRRET.pOleStr | Shell COM | `Marshal.FreeCoTaskMem` |

- COM 对象释放包装在 `SafeReleaseComObject` 辅助方法中，带 null 检查；
- PIDL 在 `finally` 块中通过 `Marshal.FreeCoTaskMem` 释放；
- 单个项目读取失败时，该项目的 PIDL 和 COM 资源在当前迭代内释放；
- 连接桌面视图失败时，所有中间 COM 对象在 `finally` 块中释放。

## 图标坐标的语义

- `IFolderView2.GetItemPosition` 返回的是桌面视图中图标的 **左上角锚点坐标**；
- 坐标单位是 **虚拟桌面物理像素**（与 M1-01 的显示器 Bounds / WorkArea 坐标系一致）；
- 图标位置是相对桌面视图原点的，而桌面视图原点在所有显示器的包围盒左上角；
- 因此，**当副屏位于主屏左侧时，其上的图标 X 坐标为负值**；
- 不需要自行乘除 DPI 缩放值（Shell 已在物理像素坐标系下返回）。

## 桌面图标是否只存在于某一显示器

**实测结论：**

- Explorer 允许桌面图标跨多显示器摆放；
- 图标可以放置在任何显示器的任意位置；
- 图标位置由用户拖拽或右键"排序方式"决定；
- 即使某显示器上没有图标，视图仍覆盖整个虚拟桌面；
- 图标坐标与显示器 Bounds 处于同一虚拟桌面物理像素坐标系。

## 图标身份处理

### StableId 优先级

1. **ParsingName（文件系统项目）**：规范化完整路径（小写、反斜杠统一），如 `c:\users\xxx\desktop\report.docx`；
2. **ParsingName（非文件系统项目）**：Shell 命名空间路径，如 `::{645FF040-5081-...}` 格式；
3. **回退**：`display:{DisplayName}` 格式（PoC 阶段让步）。

### 正确面对的场景

| 场景 | 处理方式 |
|------|---------|
| 回收站、此电脑等系统图标 | `SFGAO_FILESYSTEM` 为 false → `IsSystemItem = true` |
| 普通文件 | `SFGAO_FILESYSTEM` 为 true，`SFGAO_LINK` 和 `SFGAO_FOLDER` 均为 false |
| 文件夹 | `SFGAO_FOLDER` 为 true |
| .lnk 快捷方式 | `SFGAO_LINK` 为 true → `ItemKind.Shortcut` |
| 桌面显示名与真实文件名不同 | 使用 `SHGDN_NORMAL` 获取显示名，`SHGDN_FORPARSING` 获取解析名；ParsingName 作为 StableId 基础 |
| OneDrive 重定向桌面 | Shell COM 自动处理重定向，ParsingName 返回真实路径 |
| 公共桌面 + 用户桌面 | Shell COM 视图已合并，单个枚举即可获取所有可见图标 |
| 同名文件（名称冲突） | ParsingName（完整路径）不同 → StableId 不同 |

### 当前 StableId 的稳定性

- **会话内**：ParsingName 在单次会话内稳定；
- **跨 Explorer 重启**：ParsingName 基于文件和 Shell 路径，持久稳定；
- **非文件系统项目**：`::{...}` 格式的 Shell 路径在系统层面稳定；
- **已知限制**：`display:{DisplayName}` 回退方案在重命名后会失效（仅 PoC 让步）。

## 线程与 COM 处理

- Shell COM 需要 STA（单线程公寓）；
- WPF UI 线程是 STA，但直接调用会阻塞 UI；
- 使用 `StaThreadExecutor.RunAsync()` 在**专用后台 STA 线程**上执行 Shell COM：
  ```csharp
  var icons = await _staExecutor.RunAsync(() => _desktopIconReader.ReadDesktopIcons());
  ```
- COM 对象不在公寓间传递；所有 COM 操作在同一个 STA 线程内完成；
- 仅将纯 Core DTO（`DesktopIconDescriptor`）传回 UI 线程。

## 架构分层

### 新增文件

```
DesktopAgent.Core/
  DesktopIcons/
    DesktopIconDescriptor.cs        — 图标只读描述 (record)
    ItemKind.cs                     — 图标类型枚举
    DisplayAssignmentService.cs     — 纯逻辑显示器归属判断

DesktopAgent.WindowsInterop/
  DesktopIcons/
    IDesktopIconReader.cs           — 图标读取接口
    DesktopShellIconReader.cs       — Shell COM 实现
    ShellComInterfaces.cs           — COM 接口定义 (ShellWindowsLateBound, IServiceProvider, IShellBrowser, IShellView, IFolderView2, IShellFolder, IEnumIDList) + NativeShellMethods + PIDL 辅助方法
    StaThreadExecutor.cs            — STA 线程执行器

DesktopAgent.Poc/
  MainWindow.xaml                   — 图标列表 UI + 读取按钮
  MainWindow.xaml.cs                — 异步读取逻辑 + 显示器归属
  App.xaml.cs                       — DI 注册 IDesktopIconReader, StaThreadExecutor

DesktopAgent.Tests/
  DesktopIcons/
    DisplayAssignmentServiceTests.cs  — 16 个显示器归属测试
```

### 边界检查

| 边界 | 状态 |
|------|------|
| Core 不引用 WPF / Win32 / WindowsInterop | ✅ DesktopIconDescriptor, ItemKind, DisplayAssignmentService 均为纯 C# |
| WindowsInterop 不包含 UI 逻辑 | ✅ 仅 Shell COM 与 P/Invoke |
| Poc 只负责表现与交互 | ✅ 仅 WPF 绑定与事件处理 |
| COM 类型不暴露到 Core | ✅ DesktopIconDescriptor 不含 PIDL/HWND/COM 对象 |
| 不引入 MVVM 框架或第三方 UI 库 | ✅ 无新增 PackageReference |

## 手动验收步骤

### 前置准备

1. 桌面放置以下测试项目：
   - 一个普通文件（如 `test.txt`）
   - 一个文件夹（如 `MyFolder`）
   - 一个 .lnk 快捷方式
2. 确保系统图标（回收站等）在桌面可见
3. 如果有多显示器，副屏放在主屏左侧

### 验收流程

1. 启动 PoC：
   ```powershell
   dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj
   ```

2. **显示器拓扑**：
   - 显示器 DataGrid 正确列出所有显示器
   - 主显示器标记为 ● 目标
   - 可切换目标显示器

3. **读取图标**：
   - 点击"读取桌面图标"按钮
   - 按钮在读取期间禁用
   - 状态栏显示"正在通过 Shell COM 读取桌面图标…"
   - 读取完成后图标列表显示在 DataGrid 中

4. **核对图标信息**：
   - 显示名称与桌面实际名称一致
   - 图标数量与桌面可见图标数一致
   - 回收站等系统图标：类型 = SystemItem，文件系统 = false
   - 文件夹：类型 = Folder
   - .lnk 文件：类型 = Shortcut
   - 普通文件：类型 = File

5. **核对坐标**：
   - X、Y 坐标大致对应图标在桌面上的排列位置
   - 左上角图标坐标较小
   - 副屏上的图标有相应的坐标偏移

6. **核对显示器归属**：
   - "所属显示器"列显示正确的 DeviceName
   - 属于目标显示器的图标显示 ★ 标记

7. **负坐标验证**（多屏）：
   - 将副屏放置在主屏左侧
   - 将某图标拖到副屏
   - 重新读取 → 该图标 X 坐标为负值
   - 显示器归属正确

8. **重启 Explorer 后重新读取**：
   - `taskkill /f /im explorer.exe && start explorer.exe`
   - 点击"读取桌面图标"
   - 图标正常显示，StableId 不变

9. **确认只读**：
   - 读取前后桌面图标位置无变化
   - 图标选择状态无变化
   - 未弹出任何系统对话框

## 实际读取结果（记录模板）

| 项目 | 预期 | 实际 | 备注 |
|------|------|------|------|
| 普通文件 | File, 文件系统 true | | |
| 文件夹 | Folder, 文件系统 true | | |
| .lnk 快捷方式 | Shortcut, 文件系统 true | | |
| 回收站 | SystemItem, 文件系统 false | | |
| 此电脑 | SystemItem, 文件系统 false | | |
| 图标总数 | | | |
| 失败数 | 0 | | |
| 坐标含负值（多屏） | 副屏图标 X < 0 | | |
| 显示器归属 | 正确匹配 | | |

## 已知限制

| 限制 | 说明 |
|------|------|
| **FindWindowSW 主路径** | 使用 SWC_DESKTOP + IServiceProvider.QueryService(SID_STopLevelBrowser)；失败时自动降级到 Item 枚举 + Progman 类名检测 |
| **降级路径依赖 Progman 窗口类名** | 类名 `"Progman"` 是 Windows 传统实现细节；微软可在未来版本中更改 |
| **DesktopShellIconReader 依赖 Explorer 运行** | 如果 Explorer 崩溃或被终止，IShellWindows 中找不到桌面视图；需 Explorer 重启后重新读取 |
| **StableId 回退方案** | 当 ParsingName 不可用时使用 `display:{DisplayName}` 回退，重命名后失效 |
| **图标类型判断** | 依赖 Shell 属性位；某些虚拟文件夹可能在属性位上表现不典型 |
| **图标位置语义** | IFolderView2.GetItemPosition 返回图标左上角锚点，非中心点 |
| **OneDrive 按需文件** | OneDrive 按需文件可能显示为普通文件（有文件系统路径），但实际内容在网络 |
| **桌面图标自动排列** | 在自动排列模式下，位置由 Explorer 自动管理；读取到的位置是算法计算值 |
| **DPI 缩放** | 坐标已为物理像素，无需手动缩放；但若未来发现某些 Shell 版本返回逻辑坐标，需调整 |
| **STA 线程** | StaThreadExecutor 每次调用创建新线程；正式版可改为 STA 线程池 |
| **仅 PoC 范围** | 不读取或移动图标、不保存/恢复布局、不创建分区覆盖层、不解析快捷方式目标、不接入 AI |
