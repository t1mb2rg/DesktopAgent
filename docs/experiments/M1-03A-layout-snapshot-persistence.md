# M1-03A：桌面布局快照领域模型和 SQLite 持久化

## 实验目标

在 M1-02 只读图标读取的基础上，建立桌面布局快照的领域模型和持久化存储，为 M1-03B 接入 PoC 保存快照功能提供数据基础。

## 路线

- **M1-03A**：领域模型和 SQLite 持久化
- **M1-03B**：PoC 保存快照与快照列表
- **M1-04**：小范围图标移动与恢复验证

## 为什么保存快照必须先于图标移动

M1 的核心安全原则之一：**每次批量操作先保存快照**。在移动任何桌面图标之前，必须完整保存当前布局状态。只有确认快照写入成功后，才允许执行图标操作。恢复时通过加载快照将图标还原到原位置。

本阶段是 M1-03A（持久化层），M1-03B 才接入 PoC 保存按钮。
M1-03A 不移动图标、不恢复布局，仅验证模型和存储层的正确性。

## 快照数据结构

### DesktopLayoutSnapshot

| 字段 | 类型 | 说明 |
|------|------|------|
| SnapshotId | Guid | 唯一标识，构造时不允许 Empty |
| CreatedAtUtc | DateTimeOffset | UTC 创建时间 |
| TargetMonitorDeviceName | string? | 目标显示器 DeviceName |
| ConnectionMethod | ConnectionMethod | Shell 连接方式 |
| FormatVersion | int | 格式版本，当前固定为 1 |
| Icons | IReadOnlyList\<DesktopIconSnapshotItem\> | 图标项不可变列表 |

### DesktopIconSnapshotItem

| 字段 | 类型 | 说明 |
|------|------|------|
| Ordinal | int | 读取顺序（从 0 开始） |
| StableId | string | 稳定标识 |
| DisplayName | string | 显示名称 |
| ParsingName | string? | Shell 解析名称 |
| ItemKind | ItemKind | File/Folder/Shortcut/SystemItem/Unknown |
| PixelPosition | PixelPoint | 物理像素坐标 |
| MonitorDeviceName | string? | 所属显示器 |
| IsSystemItem | bool | 是否系统图标 |
| IsFileSystemItem | bool | 是否文件系统项目 |

### DesktopLayoutSnapshotSummary

列表摘要，不含完整图标数据。

### DesktopLayoutSnapshotFactory

工厂方法将 `DesktopIconDescriptor` 映射为 `DesktopIconSnapshotItem`，按顺序分配 Ordinal。SnapshotId 和 CreatedAtUtc 由 Factory 内部生成，调用方不需要提供。

```csharp
public DesktopLayoutSnapshot Create(
    IReadOnlyList<DesktopIconDescriptor> icons,
    string? targetMonitorDeviceName,
    ConnectionMethod connectionMethod);
```

## SQLite 表结构

```sql
desktop_layout_snapshots (
    snapshot_id              TEXT PRIMARY KEY NOT NULL,
    created_at_utc           TEXT NOT NULL,       -- ISO 8601 round-trip
    target_monitor_device_name TEXT NULL,
    connection_method        INTEGER NOT NULL,    -- ConnectionMethod enum
    format_version           INTEGER NOT NULL CHECK(format_version > 0),
    icon_count               INTEGER NOT NULL CHECK(icon_count >= 0)
);

desktop_layout_snapshot_items (
    snapshot_id        TEXT NOT NULL,
    ordinal            INTEGER NOT NULL,
    stable_id          TEXT NOT NULL,
    display_name       TEXT NOT NULL,
    parsing_name       TEXT NULL,
    item_kind          INTEGER NOT NULL,          -- ItemKind enum
    x                  INTEGER NOT NULL,
    y                  INTEGER NOT NULL,
    monitor_device_name TEXT NULL,
    is_system_item     INTEGER NOT NULL,          -- 0/1
    is_file_system_item INTEGER NOT NULL,         -- 0/1
    PRIMARY KEY (snapshot_id, ordinal),
    FOREIGN KEY (snapshot_id)
        REFERENCES desktop_layout_snapshots(snapshot_id)
        ON DELETE CASCADE
);

CREATE INDEX idx_snapshots_created_at_utc_desc
    ON desktop_layout_snapshots (created_at_utc DESC);
```

## 事务边界

`SaveAsync` 在单个事务中完成：

1. 直接插入父记录；
2. PRIMARY KEY 冲突转换为重复 SnapshotId 异常；
3. 插入图标记录；
4. Commit；
5. 失败时整体回滚。

任何步骤失败都会回滚，保证父记录和图标项要么全部成功，要么全部失败。

## FormatVersion 的作用

- 当前固定为 1（由 `DesktopLayoutSnapshot.CurrentFormatVersion` 常量定义）
- 读取时校验 `>= 1`，低于 1 时抛出 `InvalidOperationException`
- 保存时校验 `>= 1`，低于 1 时抛出 `ArgumentException`
- 严禁静默接受未知版本

## 数据库默认位置

```
%LocalAppData%\DesktopAgent\desktop-agent.db
```

`DesktopAgentDataPaths.DefaultDatabasePath` 提供默认路径，但 `SqliteDesktopLayoutSnapshotStore` 构造函数接受任意路径，便于测试使用临时目录。

M1-03A 不自动创建或使用真实用户数据库。

## 存储接口

```csharp
public interface IDesktopLayoutSnapshotStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveAsync(DesktopLayoutSnapshot snapshot, CancellationToken cancellationToken);
    Task<DesktopLayoutSnapshot?> GetAsync(Guid snapshotId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(int limit, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken);
}
```

## 时间、枚举和 bool 的存储格式

| 类型 | 存储格式 |
|------|---------|
| DateTimeOffset | TEXT，UTC ISO 8601 round-trip (`"O"` 格式) |
| ConnectionMethod | INTEGER |
| ItemKind | INTEGER，读取时 `Enum.IsDefined` 验证 |
| bool (IsSystemItem 等) | INTEGER，0/1 |

## 已知限制

- **M1-03A 不操作桌面**：不读取、不移动、不恢复图标
- **不接入 PoC UI**：存储层通过测试验证
- **不自动使用默认路径**：仅测试使用临时数据库
- **FormatVersion 锁定为 1**：后续格式变更需更新工厂和存储校验

## 依赖版本

- Microsoft.Data.Sqlite 10.0.10
- SQLitePCLRaw.bundle_e_sqlite3 2.1.12（固定 bundle 为解决旧传递原生 SQLite 包 `lib.e_sqlite3 2.1.11` 的 NU1903 安全警告）

## 最终验证结果

- **NuGet 漏洞检查**：无易受攻击的软件包
- **Build**：0 Warning，0 Error
- **Test**：86/86 passed
- **GitHub Actions**：通过
- **PoC**：未运行
