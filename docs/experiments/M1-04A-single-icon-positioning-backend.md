# M1-04A：单个桌面图标定位后端、安全目标位置规划器和自动化测试

## 阶段目标

在 M1-02（只读图标读取）和 M1-03B（布局快照）的基础上，建立单个桌面图标安全移动的后端能力：

1. 图标位置写入接口和 Shell COM 实现
2. 自动排列检测
3. 图标间距读取
4. StableId 到当前 PIDL 的安全唯一匹配
5. 相邻空位规划器（纯逻辑，不接触 Shell COM）
6. 完整的自动化测试

## 为什么拆分 M1-04A / M1-04B

桌面图标移动是破坏性操作，必须在做任何移动之前把后端能力完整构建并通过自动化测试。M1-04A 只实现后端 + 测试，M1-04B 才接入 PoC 并执行单个图标的实机验证。

## 官方 API

### IFolderView.SelectAndPositionItems

```
HRESULT SelectAndPositionItems(
    UINT cidl,
    PCUITEMID_CHILD_ARRAY apidl,
    POINT *apt,
    DWORD dwFlags);
```

`dwFlags` 取自 `_SVSIF` 枚举。M1-04A 仅使用 `SVSI_POSITIONITEM = 0x00000080`。

### IFolderView.GetAutoArrange

返回 `S_OK (0)` 表示自动排列开启，`S_FALSE (1)` 表示关闭。其他正值视为意外状态，禁止猜测。`ReadAutoArrangeState` 统一方法在所有调用点共享此解释。

### IFolderView.GetSpacing

返回当前图标网格间距（物理像素的 `Point` 结构）。X 或 Y ≤ 0 时拒绝。

## StableId 为什么必须重新解析为当前 PIDL

M1-02 读取操作中的 PIDL 是瞬时有效的——它们来自 `IEnumIDList.Next` 分配的 `CoTaskMemAlloc` 内存块，读取完成后即释放。此外，用户可能在两次操作之间在桌面上移动、删除或添加了图标，使得旧的枚举结果不再反映当前状态。

因此 `MoveDesktopIcon` 必须**重新枚举当前桌面视图的全部项目**，用 `ReadSingleIcon` 为每个项目构建 `DesktopIconDescriptor`，然后用 `StableId`（`StringComparison.Ordinal`）做精确匹配。

## 为什么不能缓存 M1-02 读取时的 PIDL

- PIDL 指向 `CoTaskMemAlloc` 分配的临时内存，M1-02 路径在 `finally` 中立即释放
- 两次操作之间桌面状态可能变化（图标被添加、删除、移动）
- Explorer 可能已重启，所有 PIDL 已失效

## 自动排列开启时拒绝移动

`MoveDesktopIcon` 在连接桌面视图后**调用 `ReadAutoArrangeState` 两次**：
1. 连接后、枚举前（第一次检查）
2. 枚举完成后、`SelectAndPositionItems` 之前（第二次检查，缩小竞态窗口）

不依赖之前 `GetCapabilities` 的结果。

## 为什么必须唯一匹配

`StableId` 在同一桌面视图中理论上是唯一的。0 个匹配 → 图标已被删除；≥2 个匹配 → StableId 策略缺陷或异常重复。不使用模糊匹配。

## 为什么 Writer 遇到任一枚举项目失败时整体失败

失败的项目可能就是目标，或可能造成唯一性判断错误。因此一旦任一项目元数据读取失败，整个操作终止。

## PIDL 和 COM 生命周期

- `ShellWindowsLateBound` → `using`
- `DesktopViewContext` → `using`
- `IEnumIDList` → 交给 `DesktopViewContext` 管理
- **每条迭代使用 `try/finally`**：`pidl` 在 `finally` 中 `Marshal.FreeCoTaskMem`
- **唯一匹配时所有权转移**：`matchedPidl = pidl; pidl = nint.Zero;` — 局部 pidl 清零后当前迭代 finally 不再释放
- **外层 finally** 只负责 `if (matchedPidl) FreeCoTaskMem(matchedPidl)`
- **重复匹配**：不手动释放任何 PIDL，直接抛异常；当前 pidl 由迭代 finally 释放，之前的 matchedPidl 由外层 finally 释放
- **ReadSingleIcon 异常**：由迭代 finally 释放 pidl，包装为 `InvalidOperationException`（含 1-based 序号 + 原始 InnerException）
- 不使用 `catch ... when (ex is not InvalidOperationException)` 等按异常类型分派释放
- 不使用 `Marshal.FinalReleaseComObject`

## 枚举以 IEnumIDList.Next 状态为准

```csharp
while (true)
{
    hr = enumerator.Next(1, out pidl, out fetched);
    // hr < 0 → COMException
    // hr == 1 (S_FALSE) && fetched == 0 → 结束
    // hr == 0 (S_OK) && fetched == 1 → 处理该项目
    // 其他组合 → InvalidOperationException
}
```

不使用 `for (i < itemCount)`，确保不因 itemCount 与 Next 的不一致而漏检或提前终止。即使找到第一个匹配也继续枚举，以确认无第二个匹配。

## 相邻空位规划顺序

固定候选顺序：Right → Left → Down → Up

碰撞检测使用 AABB 矩形重叠算法（非坐标完全相等），使用 `checked` 运算防止极端 int 输入静默环绕。

## Reader 仅暴露两个 internal 方法

- `DesktopShellIconReader.FindDesktopView`
- `DesktopShellIconReader.ReadSingleIcon`

`SafeReleaseComObject` 为 `private`。Writer 自备小型 `SafeReleaseObject` 辅助方法。

## GitHub Actions 首次失败

首次 Build 失败原因为 `DesktopIconPositioningCapabilities.cs` 未被 Git 跟踪（`??` 状态）。已修复。

## 本阶段没有运行任何真实移动

M1-04A 仅构建并通过自动化测试验证后端。没有图标被真实移动。

## M1-04B 将实现

- 用户显式选择单个文件系统图标
- 移动前保存安全快照
- 移动一个相邻空位
- 同一会话原位恢复
- 实机验证
