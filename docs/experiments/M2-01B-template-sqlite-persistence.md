# M2-01B：桌面布局模板 SQLite 持久化

## 阶段目标

在现有桌面布局快照数据库之上，增加布局模板的完整持久化：
存储、读取、列表、删除，以及事务原子性保证。

## 为什么模板与快照使用同一个数据库

- 两者共同构成桌面布局的完整还原上下文（快照 = 当前状态，模板 = 目标布局）
- 共享连接配置（ForeignKeys、Pooling）、路径和初始化流程
- 避免分布式事务和跨库引用

## 为什么引入共享 Schema 初始化器

`SqliteDesktopAgentSchema` 是 internal static 共享组件，统一管理 `PRAGMA user_version`。

v1 快照仅由 SnapshotStore 创建。引入 v2 模板表后，若两个 Store 各自执行
`PRAGMA user_version = N`，后执行的 Store 可能覆盖或冲突。

共享初始化器的约束：
- 仅 `SqliteDesktopAgentSchema.Initialize()` 设置 `PRAGMA user_version`
- 任一 Store 的 `InitializeAsync` 都通过它完成 Schema 迁移
- 初始化顺序不影响最终版本

## v1 → v2 的升级路径

| 初始状态 | 操作 | 结果 |
|---|---|---|
| v0（空文件） | 任一 Store InitializeAsync | user_version=2，v1+v2 表全部创建 |
| v1（仅有快照表） | 任一 Store InitializeAsync | 保留快照，新增模板表，user_version=2 |
| v2 | 任一 Store InitializeAsync | 幂等检查并补齐缺失的 v1/v2 表和索引，user_version 保持为 2 |
| v99 或更高 | 任一 Store InitializeAsync | 拒绝，抛出 InvalidOperationException |

升级在单个事务内完成。中途失败时事务回滚，user_version 不变，
不会出现半升级的 Schema。

`CREATE TABLE/INDEX IF NOT EXISTS` 能补齐完全缺失的对象。
它不会修复一个已存在但列结构被任意篡改的表（那是手动修复的范畴）。
`user_version` 为当前值时不会提前 return，始终执行补齐逻辑。

## 为什么不得由两个 Store 各自设置 user_version

若 SnapshotStore 设置 `user_version=1`，TemplateStore 设置 `user_version=2`：
- 先初始化 Snapshot 后 Template：Snapshot 升级到 v1 后，Template 再升级到 v2（正确）
- 先初始化 Template 后 Snapshot：Template 升级到 v2，Snapshot 再次执行
  `PRAGMA user_version=1` 将版本降级（错误）

统一由共享 Schema 管理，不依赖初始化顺序。

## 数据表与外键关系

```
desktop_layout_templates           (父表)
  └── desktop_layout_template_zones (子表, FK ON DELETE CASCADE)
```

每个 Zone 作为独立关系表记录存储，不使用 JSON。

## SaveAsync 的原子 Upsert 语义

单个事务内执行：
1. `INSERT ... ON CONFLICT DO UPDATE` 父记录
2. `DELETE` 所有旧 Zones
3. `INSERT` 新 Zones（ordinal = 输入索引 0..N-1）
4. Commit

事务内失败自动回滚，旧模板完整保留。

## 为什么保留 Zones 输入顺序和独立 SortOrder

`ordinal` 字段记录调用方传入的顺序。`SortOrder` 是独立字段，
用于后续规划器确定执行优先级。两者均原样保存，不做自动重排。

## Binding 的关系型表示

三个字段共同恢复：
- `target_display_binding_kind` (INTEGER, 0/1/2)
- `target_display_device_name` (TEXT, NULL 或设备名)
- CHECK 约束确保组合合法：
  - SelectedTarget/PrimaryDisplay → device_name IS NULL
  - ExactDeviceName → device_name IS NOT NULL AND TRIM != ''

恢复时通过 `TargetDisplayBinding` 静态工厂重建，不绕过构造函数。

## 数据损坏时 fail-closed

- 枚举值无效 → `InvalidDataException`（不返回部分模板）
- Guid 文本无效 → `InvalidDataException`
- 父表 zone_count 与实际行数不符 → `InvalidDataException`
- 坐标或容量无法构造 NormalizedRect/DesktopLayoutZone → 包装为 `InvalidDataException`
- 领域模型最终验证仍由 `DesktopLayoutTemplate` 构造函数执行

## 事务回滚测试

通过 SQLite trigger 在特定 Zone 插入时 `RAISE(ABORT, ...)`，
验证：
- 事务内异常时旧父记录不变
- 旧 Zones 完整保留
- 不残留部分新 Zones

## v2 版本标记仍会确保完整 Schema

即使 `user_version=2`，`Initialize()` 仍始终在事务内执行完整的 `CREATE TABLE IF NOT EXISTS` 和 `CREATE INDEX IF NOT EXISTS`。这确保：
- 表或索引被误删时，可通过 `IF NOT EXISTS` 补齐缺失对象
- user_version 不会因重复创建而改变（version==CurrentVersion 时不重新设置 PRAGMA）

## Binding 非法组合 fail-closed

`RestoreBinding` 对三种 Kind 各自验证 DeviceName 组合：
- `SelectedTarget` / `PrimaryDisplay`：deviceName 必须为 null，否则 `InvalidDataException`
- `ExactDeviceName`：deviceName 必须非 null 且非空白，否则 `InvalidDataException`

不静默忽略数据库中的任意 DeviceName 值。

## 测试不依赖 InternalsVisibleTo

`Persistence.csproj` 无 `InternalsVisibleTo`。Schema 测试通过公共 Store 行为验证：
- 调用 `SqliteDesktopLayoutSnapshotStore.InitializeAsync` 或 `SqliteDesktopLayoutTemplateStore.InitializeAsync`
- 测试自行实现 `OpenRawConnection()` 使用相同连接配置直接查询 `sqlite_master` 和 `PRAGMA user_version`
- Trigger 测试使用自己的原始连接

## Schema 通过公共 Store 行为验证

所有 Schema 测试验证公开 API 行为：InitializeAsync、GetAsync、SaveAsync 的实际结果，而非内部 DDL 实现细节。

## 本阶段边界

**实现**：
- Core: `IDesktopLayoutTemplateStore` 接口 + `DesktopLayoutTemplateSummary`
- Persistence: `SqliteDesktopAgentConnectionFactory` + `SqliteDesktopAgentSchema` + `SqliteDesktopLayoutTemplateStore`
- 现有 `SqliteDesktopLayoutSnapshotStore` 重构为使用共享 Schema
- Schema 测试（通过公共 Store 行为验证）、TemplateStore 测试（含 round-trip、upsert、事务回滚、损坏数据拒绝）、Summary 测试
- 实验文档

**不实现**：
- WPF 模板编辑器
- 图标分类和分配
- 归一化坐标 → 像素坐标解析
- 分区容量计算
- Writer 执行
- PoC 接入

## M2-02A 后续职责

- NormalizedRect → PixelRect 转换
- 网格槽位计算
- 理论容量 vs MaxItemCount 有效容量
