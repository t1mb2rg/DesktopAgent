# M1-01：任意数量显示器拓扑读取  +  目标显示器选择

## 目标

验证 Windows 11 环境中能否可靠读取：

- 显示器数量；
- 主屏标记；
- 虚拟桌面坐标；
- 工作区坐标；
- DPI 与缩放；
- 负坐标。

并在 PoC 界面中完成目标显示器选择与保持。

## 当前实现

### 底层读取

`NativeDisplayTopologyReader` 使用：

- `EnumDisplayMonitors`
- `GetMonitorInfo`
- `GetDpiForMonitor`

返回的坐标是虚拟桌面物理像素坐标，正确处理：
- 副屏位于主屏左侧产生的负 X 坐标；
- 副屏位于主屏上方产生的负 Y 坐标；
- 不同分辨率；
- 不同 DPI 缩放；
- 任意显示器排列。

### 目标显示器选择

`TargetDisplaySelector`（Core 层纯逻辑，无外部依赖）：

- 默认选择主显示器（`IsPrimary == true`）；
- 重新读取显示器后，优先按 `DeviceName` 保留原目标；
- 原目标设备不存在时自动退回主显示器；
- 没有主显示器时退回列表第一块显示器；
- 列表为空时返回 null。

### PoC 界面

- DataGrid 单选模式列出所有显示器；
- ● / ○ 列清楚标识当前目标；
- 单击行即可切换目标显示器；
- 状态栏显示当前目标显示器的完整信息；
- 点击"重新读取"后，优先保持原目标选择。

### PerMonitorV2 DPI Awareness

通过 `app.manifest` 声明 `PerMonitorV2`，不在业务代码中调用 `SetProcessDpiAwareness`，避免上下文冲突。

## 架构分层

```
DesktopAgent.Core
  ├─ Displays/
  │   ├─ DisplayDescriptor        (record — 显示器只读描述)
  │   ├─ IDisplayTopologyReader   (接口 — 读取拓扑)
  │   └─ TargetDisplaySelector    (static — 纯逻辑选择)
  └─ Geometry/
      ├─ PixelPoint
      └─ PixelRect

DesktopAgent.WindowsInterop
  └─ Displays/
      └─ NativeDisplayTopologyReader  (Win32 P/Invoke 实现)

DesktopAgent.Poc
  ├─ app.manifest                 (PerMonitorV2 DPI Awareness)
  ├─ MainWindow.xaml              (WPF 表现)
  └─ MainWindow.xaml.cs           (交互逻辑)

DesktopAgent.Tests
  ├─ Geometry/PixelRectTests.cs
  └─ Displays/TargetDisplaySelectorTests.cs
```

## 人工验收

- [ ] 单屏环境：数量正确，该显示器自动成为目标
- [ ] 双屏环境：数量正确，默认选择主屏，可切换目标
- [ ] 三屏环境：数量正确
- [ ] 左侧显示器坐标为负时读取正确
- [ ] 上方显示器 Y 坐标为负时读取正确
- [ ] 不同 DPI 缩放读取正确
- [ ] 主屏标记正确
- [ ] 断开/重新连接显示器后点击"重新读取"结果正确
- [ ] 选择非主屏副屏 → 重新读取 → 目标保持
- [ ] 选择副屏 → 断开该副屏 → 重新读取 → 目标退回主屏
- [ ] 目标显示器信息在状态栏中正确显示
- [ ] 多显示器不同 DPI 缩放下 PoC 窗口外观正常

## 手动验收步骤

1. 启动 PoC：
   ```powershell
   dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj
   ```

2. 验证初次加载：
   - DataGrid 列出所有显示器
   - 主显示器行显示 ● 标记
   - 状态栏显示目标显示器信息
   - 显示器数量与实际连接数一致

3. 验证目标切换：
   - 单击任意非主屏行
   - 该行显示 ●，原目标行显示 ○
   - 状态栏更新为目标显示器信息

4. 验证重新读取保持目标：
   - 选择一个非主屏显示器作为目标
   - 点击"重新读取"
   - 该显示器仍为 ● 目标（若仍连接）

5. 验证目标回退：
   - 选择一个副屏作为目标
   - 物理断开该副屏（或通过 Windows 显示设置禁用）
   - 点击"重新读取"
   - 目标自动退回主显示器 ●

6. 验证负坐标：
   - 在 Windows 显示设置中将副屏拖到主屏左侧或上方
   - 重新读取
   - 坐标范围列显示负值（如 `(-1920, 0)  1920 × 1080`）

## 当前已知限制

- **DeviceName 稳定性**：`\\.\DISPLAYx` 在 PoC 阶段作为会话内标识是够用的，
  但在某些场景下（如显卡驱动更新、切换显卡输出）编号可能变化。
  正式版需补充更稳定的设备身份（如 EDID 序列号、容器 ID 或持久化的用户指定名称）。
- **DPI Awareness**：PerMonitorV2 已通过 app.manifest 声明，但 WPF 的 PerMonitorV2
  支持在某些极端场景（如混合 DPI 环境中的窗口拖拽）可能仍有像素对齐问题。
- **显示器热插拔**：当前仅在用户点击"重新读取"时刷新拓扑，未监听 `WM_DISPLAYCHANGE`。
- **仅 M1 PoC 范围**：不读取或移动桌面图标，不实现透明分区层，不接入 AI。
