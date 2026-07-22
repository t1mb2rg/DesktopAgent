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

返回 `S_OK (0)` 表示自动排列开启，`S_FALSE (1)` 表示关闭。其他正值视为意外状态，禁止猜测。

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

`MoveDesktopIcon` 在连接桌面视图后**必须再次调用 `GetAutoArrange`**，不能只依赖之前 `GetCapabilities` 的结果。如果用户在这两次调用之间修改了桌面设置，依赖旧结果会导致在自动排列开启时执行移动操作，Explorer 将忽略或覆盖该移动。

## 为什么必须唯一匹配

`StableId` 在同一桌面视图中理论上是唯一的（基于 `ParsingName` 或 `DisplayName`）。如果出现 0 个匹配，说明目标图标可能已被删除；如果出现 ≥2 个匹配，说明 `StableId` 策略有缺陷或存在异常重复。两种情况都不应执行移动。

不使用模糊匹配（`StartsWith`、`Contains`、忽略大小写等），避免误移动无关图标。

## 为什么 Writer 遇到任一枚举项目失败时整体失败

只读 `DesktopShellIconReader` 可以在个别项目读取失败时跳过它并继续，因为其目标是读取"尽可能多"的图标。

`DesktopShellIconPositionWriter` 不同——失败的项目可能就是目标，或可能造成唯一性判断错误（例如目标成功匹配但另一个同名项目读取失败，导致唯一性检查不完整）。因此一旦任一项目失败，整个操作终止。

## PIDL 和 COM 生命周期

- `ShellWindowsLateBound` → `using`
- `DesktopViewContext` → `using`
- `IEnumIDList` → 交给 `DesktopViewContext` 管理
- 每个 `Next` 返回的 PIDL → 不匹配的立即 `Marshal.FreeCoTaskMem`
- 匹配 PIDL → 在 `SelectAndPositionItems` 和 `GetItemPosition`（实际位置）完成后，于 `finally` 中精确释放一次
- 重复匹配时立即释放新的 PIDL，然后释放之前缓存的匹配 PIDL
- 不使用 `Marshal.FinalReleaseComObject`

## 相邻空位规划顺序

固定候选顺序：Right → Left → Down → Up

每个候选位置的图标单元（以 spacing 为大小的矩形）必须：
1. 完整位于 workArea 内
2. 不与任何已占用位置的图标单元发生矩形重叠

碰撞检测使用矩形重叠算法（不是坐标完全相等），因为图标可能未严格对齐网格。

## 本阶段没有运行任何真实移动

M1-04A 仅构建并通过自动化测试验证后端。没有图标被真实移动。

## M1-04B 将实现

- 用户显式选择单个文件系统图标
- 移动前保存安全快照
- 移动一个相邻空位
- 同一会话原位恢复
- 实机验证
