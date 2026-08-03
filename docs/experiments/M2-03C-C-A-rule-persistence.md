# M2-03C-C-A：桌面图标规则持久化

## 阶段目标

实现纯 Core + Persistence 的规则持久化能力：
- SQLite Schema v3（新增规则表）
- Core Store 接口 `IDesktopLayoutIconRuleStore`
- SQLite Rule Store 实现
- 完整 CRUD / 原子替换
- v0/v1/v2 → v3 无损升级
- 损坏数据 fail-closed

## Schema v3 表结构

```sql
CREATE TABLE IF NOT EXISTS desktop_layout_template_icon_rules (
    template_id     TEXT NOT NULL,
    stable_id       TEXT NOT NULL,
    rule_kind       INTEGER NOT NULL
        CHECK(rule_kind IN (0, 1, 2)),
    target_zone_id  TEXT NULL,
    PRIMARY KEY (template_id, stable_id),
    FOREIGN KEY (template_id)
        REFERENCES desktop_layout_templates(template_id)
        ON DELETE CASCADE,
    CHECK(
        (rule_kind IN (0, 1) AND target_zone_id IS NULL)
        OR
        (rule_kind = 2 AND target_zone_id IS NOT NULL)
    ),
    CHECK(stable_id != '')
);
```

- `rule_kind`：0=Exclude, 1=Pin, 2=ZoneOverride（与 `DesktopLayoutIconRuleKind` 枚举对齐）
- `stable_id`：BINARY 语义（SQLite 默认），区分大小写，不 Trim
- PK `(template_id, stable_id)` 保证同一模板内 StableId 唯一

## 为什么规则使用独立 Store

- 规则不属于 `DesktopLayoutTemplate` 领域模型（模板纯粹是分区定义）
- 独立 Store 不破坏现有 `IDesktopLayoutTemplateStore` API
- 规则有自己的 CRUD 语义（原子完整替换 vs Upsert）
- M2-03C-C-B 的 WPF 阶段将协调 Template + Rule Store

## 为什么不添加 Zone ON DELETE CASCADE 外键

当前模板 `SaveAsync` 先删除全部旧 Zone 再重新插入。如果规则表对 Zone 有级联删除外键，普通模板保存会意外删除 ZoneOverride 规则。

ZoneOverride 的目标 Zone 一致性由 Rule Store 在保存（事务内显式查询）和读取（逐个验证）时保证。

## Save / Get / Delete 语义

### SaveAsync
- 按 TemplateId 原子完整替换规则集合
- 先验证模板存在
- 再验证所有 ZoneOverride 的 TargetZone 存在且属于同一模板
- 验证完成后在同一事务内删除旧规则、插入新规则
- 保存空 RuleSet = 删除该模板全部规则
- 模板不存在时拒绝（即使空 RuleSet）

### GetAsync
- 模板不存在 → `null`
- 模板存在但无规则 → `DesktopLayoutIconRuleSet.Empty(templateId)`
- 读取时对每条记录严格验证：
  - `rule_kind` 为已定义枚举值
  - Exclude/Pin 必须 `target_zone_id IS NULL`
  - ZoneOverride 必须 `target_zone_id` 非 NULL 且为有效 Guid
  - ZoneOverride 的目标 Zone 必须存在且属于当前模板
- 所有损坏数据抛 `InvalidDataException`

### DeleteAsync
- 只删除规则，不删除模板
- 返回是否实际删除至少一条规则

## 空规则集与模板不存在的区别

| 状态 | GetAsync 返回 |
|---|---|
| 模板不存在 | `null` |
| 模板存在，无规则 | `DesktopLayoutIconRuleSet.Empty(templateId)` |
| 模板存在，有规则 | 完整 `DesktopLayoutIconRuleSet` |

## 原子替换流程

```
BEGIN TRANSACTION
1. 验证模板存在（SELECT）
2. 验证所有 ZoneOverride TargetZone（SELECT per Override）
3. 删除旧规则（DELETE WHERE template_id = @tid）
4. 插入新规则（INSERT per rule）
COMMIT
```

任一步骤失败 → 事务回滚 → 旧规则完整保留。

## StableId BINARY / Ordinal 语义

- 保存：原样写入，不 Trim，不大小写转换
- 读取：原样恢复，使用 `StringComparer.Ordinal` 语义
- ORDER BY：`stable_id COLLATE BINARY ASC`
- 前后空格、纯空格均保留（与领域模型一致）

## 损坏数据 fail-closed

- 未定义 `rule_kind` → `InvalidDataException`
- Exclude/Pin 带 `target_zone_id` → `InvalidDataException`
- ZoneOverride 缺 `target_zone_id` → `InvalidDataException`
- 无效 Guid → `InvalidDataException`
- 悬空 Zone 引用 → `InvalidDataException`
- 领域构造失败 → 包装为 `InvalidDataException`
- 不静默修复，不恢复为空规则集

## v0/v1/v2 → v3 迁移

- 所有迁移单事务完成
- v0 → 创建全部 v1+v2+v3 表
- v1 → 创建 v2+v3 表，保留快照数据
- v2 → 创建 v3 表，保留模板数据
- v3 重复初始化幂等（`CREATE TABLE IF NOT EXISTS`）
- future version 拒绝（在任何 DDL 前）

## 测试数量与结果

### Schema v3 测试：29 个（12 新增）
- v0/v1/v2 → v3 迁移（4）
- 幂等初始化、重复初始化（2）
- 未来版本拒绝（2）
- 规则表结构验证（PK、字段）
- FK 级联删除
- rule_kind CHECK 约束
- kind/target_zone_id 组合 CHECK
- 大小写 StableId 共存
- 重复 StableId PK 拒绝
- 不同模板相同 StableId 允许

### Rule Store 测试：43 个
- 构造器路径验证、初始化幂等（2）
- 三类规则 round-trip（4）
- StableId 精确语义：大小写、空格、纯空格（3）
- BINARY 排序（1）
- 完整替换、空规则集、空规则读取（3）
- 模板不存在处理（3）
- Delete 语义（3）
- 级联删除（1）
- 模板重新保存规则不丢失（1）
- ZoneOverride 验证（5）
- 损坏数据 fail-closed（9）
- 原子性、取消（6）
- 输入验证（3）

### 全量测试：1108/1108 通过，0 失败

## 安全边界

- 不调用 Writer
- 不移动真实桌面图标
- 不保存安全快照
- 不修改 Persistence 之外项目
- 不修改 Poc / WindowsInterop
- 不运行 GUI

## M2-03C-C-B 后续范围

- WPF 规则编辑器
- Preview 规则接入
- App.xaml.cs / MainWindow 依赖注入
- 模板 Store 和 Rule Store 在 UI 保存流程中协调
