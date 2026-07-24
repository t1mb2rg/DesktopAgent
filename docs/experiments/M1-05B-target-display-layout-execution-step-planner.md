# M1-05B：指定显示器布局恢复执行步骤规划器

## 阶段目标

将 M1-05A 生成的 `TargetDisplayLayoutRestorePlan` 转换为确定性的原子移动步骤序列 `TargetDisplayLayoutExecutionPlan`，包含直接移动、临时中转和最终目标移动。

## 为什么 M1-05B 仍不调用 Writer

执行计划是纯逻辑产物。M1-05B 在没有任何 Shell COM 调用的前提下，通过内存模拟验证完整移动序列的可执行性。Writer 调用留到 M1-05C。

## 为什么需要完整当前桌面作为输入

M1-05A 生成的计划可能在生成后被外部因素（用户操作、Explorer 重启等）陈旧化。M1-05B 需要独立验证每个计划项对应的当前状态是否与计划中的假设一致。

## 原子步骤模型

三种步骤类型：
- `MoveToTarget`：从当前位置直接移动到快照目标
- `MoveToTemporaryStaging`：移动到临时中转位置
- `MoveFromTemporaryStagingToTarget`：从中转位置移动到快照目标

## 内存占用模拟

维护所有当前图标的坐标映射和占用矩形。每生成一步后更新模拟状态。后续所有直接移动判定和中转位置搜索都基于当前模拟状态。

## 临时中转位置搜索

网格扫描从 workArea 左上角开始，步长 = spacing。候选必须不与任何模拟占用或最终目标矩形重叠。使用同一套 `DesktopIconLayoutGeometry` 语义。

## 动态释放和复用

前一个中转位置在图标离开后立即释放，后续环可以复用。不需要预先分配或预先保留专属中转位置。

## 无环顺序

当源计划存在 `MoveExecutionOrder` 且无环时，算法直接使用该顺序执行所有 `MoveToTarget` 步骤，不生成任何中转步骤。

## 环的通用展开

两图标交换 → 3 步（A → staging, B → target, A → target）。三图标环 → 4 步。算法通过通用占用模拟支持任意长度的简单环。

## 最终状态验证

所有步骤生成后，验证每个 MoveRequired 项的模拟位置等于其快照目标。不一致时返回 `FinalStateMismatch` 且 Steps 为空。

## 失败时不返回半截步骤

任何检查失败、模拟死锁或验证不通过时，`IsExecutable = false` 且 `Steps` 为空。不可执行计划不返回部分步骤。

## M1-05C 将负责

- 调用 `DesktopShellIconPositionWriter.MoveDesktopIcon`
- 每步执行后回读实际位置
- 处理 Writer 异常与回滚
- 实机验证
