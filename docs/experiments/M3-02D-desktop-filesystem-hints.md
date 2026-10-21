# M3-02D：Desktop File-System Observation Hint Source

- 日期：2026-08-08
- 状态：代码阶段完成，等待 CI 与 Windows 实机验收
- 范围：M3-A，只读 hint source

## 目的

接入 M3 后台观察链的第一个真实 Windows 事件来源：桌面顶层文件系统条目的创建、删除和重命名。

```text
User Desktop / Public Desktop
→ FileSystemWatcher
→ DesktopFileSystemObservationHintSource
→ (FileSystem, "DesktopFileSystemWatcher")
→ M3 background pump
→ fresh Observation
```

该来源只负责表达：

> 桌面文件系统条目可能发生了变化，值得重新观察。

它不提供当前桌面事实。

## 监听目录

默认目录通过 Windows SpecialFolder 获取：

- `Environment.SpecialFolder.DesktopDirectory`
- `Environment.SpecialFolder.CommonDesktopDirectory`

这可以适配用户 Desktop 被系统重定向后的实际路径，而不是硬编码 `C:\Users\...\Desktop`。

目录会：

- 忽略空值；
- `Path.GetFullPath()` 规范化；
- 去除末尾目录分隔符；
- 使用 `OrdinalIgnoreCase` 去重；
- 稳定排序。

因此若用户 Desktop 与 Public Desktop 因异常环境解析成同一路径，也不会创建重复 watcher。

## 监听事件

`FileSystemWatcher` 配置：

```text
IncludeSubdirectories = false
NotifyFilter = FileName | DirectoryName
```

监听：

- Created
- Deleted
- Renamed
- Error

刻意不监听普通 `Changed` 内容变化。

原因：文件内容被修改通常不会改变桌面图标集合或布局；把所有内容写入都当成 hint 会制造大量与整理无关的噪声。

## 隐私 / 事实边界

底层 `FileSystemEventArgs` / `RenamedEventArgs` 中包含的路径和文件名会在 adapter 内立即丢弃。

向上层只发布：

```text
Kind = FileSystem
Source = "DesktopFileSystemWatcher"
```

watcher infrastructure error 则发布：

```text
Kind = Unknown
Source = "DesktopFileSystemWatcher"
```

异常对象本身也不会进入 hint payload。

因此事件 payload 不可能绕过 fresh observation 直接进入：

- DesktopObservation
- baseline
- State Diff
- Suggestion

## 为什么 Error 也发 hint

`FileSystemWatcher.Error` 可能表示内部缓冲区溢出或 watcher 基础设施异常。

此时不能相信“之前没收到事件 = 桌面没变化”。

因此 Error 只发一个 `Unknown` hint，让上层稍后进行一次完整 fresh observation，以当前事实重新收敛。

## 部分失败策略

用户 Desktop 与 Public Desktop 独立启动 watcher。

如果其中一个目录：

- 不存在；
- 无访问权限；
- watcher 创建失败；
- watcher Start 失败；

另一个成功 watcher 仍继续运行。

`DesktopFileSystemHintSourceStartResult` 返回：

- IsRunning
- ActiveWatcherCount
- WatchedDirectories
- Issues

只有所有 watcher 都失败时 `IsRunning=false`。

这与 M3 的 observation-first 思路一致：hint source 可以降级，但不能把部分事件覆盖伪装成完整事实。

## 生命周期

- `Start()`：启动所有可用目录 watcher；运行中重复 Start 幂等返回当前结果；
- `Stop()`：先标记停止，再退订、Stop、Dispose 所有 watcher；
- `Stop()` 后晚到的底层 callback 不再发布 hint；
- `Stop()` 后允许重新 `Start()` 并创建新 watcher；
- `Dispose()` 后禁止再次 Start。

上层 publish callback 的异常会在 watcher callback 边界吞掉，避免 `FileSystemWatcher` 线程因为 observer/pump 异常退出。

## 自动测试

使用 fake watcher factory，不依赖真实 OS watcher 时序，覆盖：

- 路径规范化、大小写去重、稳定排序；
- 两个目录正常启动；
- 运行中 Start 幂等；
- Changed 只发布 FileSystem + 固定 provider；
- Error 只发布 Unknown + 固定 provider；
- exception/path payload 不进入 hint；
- publish callback 抛异常不逃逸到 watcher；
- 部分目录失败继续运行；
- 全部失败返回 NotRunning；
- watcher Start 失败时正确释放该 watcher；
- Stop 后不再发布晚到事件；
- Stop → Start 使用新 watcher；
- Dispose 后 Start 被拒绝。

## 明确的覆盖缺口

FileSystemWatcher **检测不到纯图标位置移动**。

用户在桌面上拖动一个已有图标时：

- 文件名没变；
- 文件目录没变；
- 文件系统通常没有 Created / Deleted / Renamed；
- 因此本 hint source 不会触发。

这不是 bug，而是来源能力边界。

M3 后续仍需要组合：

1. display topology/settings hint；
2. Shell-specific hint（若可靠）；
3. periodic fallback fresh observation。

periodic fallback 尤其重要，它保证即使某类 Windows 事件没有可靠通知来源，DesktopAgent 最终仍能重新观察并收敛到真实桌面状态。

## 下一步

M3-02E 再把这个 source 接入 PoC 生命周期和 `DesktopObservationBackgroundPump`：

```text
DesktopFileSystemObservationHintSource
→ pump.TryPublishHint(...)
→ 02A debounce/max-delay
→ 02B single-flight
→ 01D stable observation/baseline/diff
```

初次接入仍应限定 Debug / PoC，并把后台结果显示到 diagnostics UI；不解除 unattended Writer gate。
