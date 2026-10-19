# F-3：重新生成预览时刷新桌面起点

## 已确认的代码路径

`AiAssistantView.MultiDisplayImport.cs` 每次生成都传入 `_latestSuccessfulUnderstanding` 中的 Draft 与 Observation。修改前，`AiDesktopWorkspacePlanningService` 直接使用这份缓存 Observation 建立 Geometry、ItemFacts 与 Preview SourceSnapshot，没有重新读取桌面。

因此，在理解完成后移动图标（包括成功应用布局后），再次生成会继续使用旧位置。Apply Coordinator 独立 fresh capture 时正确拒绝旧起点；单纯重新生成并不能更新这份缓存起点。这是一个已确认可形成重复拒绝的路径，尚不能证明历史截图中的每个差异都由它导致。

## 修改

- 生成前使用只读 WindowsDesktopObservationSource 重新捕获桌面，STA executor 在捕获结束后释放。
- Geometry 和实测网格事实均依据 fresh Observation；新 Preparation 接口明确区分语义来源 Observation 与 fresh Observation。
- 使用现有语义来源指纹判断旧分类是否可复用。纯位置、屏幕变化允许复用；名称、类型、系统身份或项目集合变化要求重新理解。
- SourceSnapshot 和 ItemFacts 来自 fresh Observation，并继续验证 Geometry 与 ObservationId 一致。
- fresh check 拒绝诊断显示字段级预期/实际值，准确统计变化数量，仅展示按 StableId 排序的前五项详情。
- 未改变用户确认、一次性入口消费、执行前严格 fresh check、Writer、恢复互锁或回滚语义。

## 验证

- 针对性测试：22 项通过。
- Release 全量：2464 passed / 0 failed / 0 skipped（原基线 2453，新增 11 个用例）。
- Debug / Release 全解决方案构建通过。
- 现有 Coordinator 回归用例继续证明：首次 Writer 前发生漂移时，无 Writer 调用、无安全快照和执行 journal 写入。
- 新用例覆盖跨屏移动后的 fresh 起点和语义保留、改名/类型/系统身份/增删项目拒绝、旧 Geometry 不能与新位置混用、单像素漂移诊断、不同字段诊断及超过五项时的准确计数。
- 一次全量运行因新增源码合同测试使用 LF 匹配 CRLF 而失败；归一化换行后重跑全量通过。

本机证据（gitignore 排除）：

- `artifacts/release-baseline/f3-targeted.log`
- `artifacts/release-baseline/f3-release.trx`
- `artifacts/release-baseline/f3-full-test.log`
- `artifacts/release-baseline/f3-release-build.log`
- `artifacts/release-baseline/f3-debug-build.log`

构建为增量构建，本轮输出警告数不能直接与最初 132 条基线比较。

## 尚需真机复验

1. 完成一次桌面理解，然后改变图标位置，再生成预览；确认新预览起点反映当前桌面。
2. 在桌面稳定时确认应用；若仍被拒绝，记录新的字段级预期/实际差异。
3. 在预览形成后移动一个图标，确认 Apply 仍拒绝写入并给出准确差异。
4. 增删或改名项目后生成，确认要求重新理解。

本轮没有启动产品、调用 AI Provider 或移动真实桌面图标。AI 设计期间或预览形成后再次发生的真实桌面变化仍会被 Apply 正常拒绝；该行为属于安全边界。
