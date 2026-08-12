# Tessel UI Copy / Branding Migration Checklist

> Status: implementation checklist  
> Recorded: 2026-08-12  
> Scope: public-facing WinUI product copy and product identity only

这份清单记录 Tessel 正式产品 Surface 后续需要统一的名字、文案和用户可见术语。

它不要求现在立刻重命名稳定的 `DesktopAgent.*` 工程结构，也不改变 Writer、Recovery、Automation Policy 或任何安全合同。

## 1. 基本规则

Tessel 的正式 UI 应该像一个 Windows 工具在和用户说话，而不是把内部对象模型直接打印出来。

优先规则：

- 先告诉用户发生了什么，再考虑内部实现叫什么；
- 能用正常中文表达的地方，不把 enum、store、draft、provider、observation 等内部名直接放在主界面；
- 安全提示要直接，不用模糊的“智能”“可信”“增强”等词代替真实边界；
- 用户可见文案区分“预览”“保存偏好”“真实桌面执行”，不要让三个动作看起来像同一件事；
- 诊断工作台可以继续保留工程术语，正式 Tessel UI 不需要迁就 Diagnostics 的表达；
- 还没实现的能力不能写成当前已经可以使用。

## 2. 品牌名迁移

对外产品名：

```text
Tessel
```

第一批应迁移的用户可见位置：

| Surface | 当前 | 目标 |
|---|---|---|
| WinUI 主窗口标题 | `DesktopAgent` | `Tessel` |
| 概览页产品标题 | `DesktopAgent` | `Tessel` |
| Tray Tooltip | `DesktopAgent` | `Tessel` |
| Tray 默认菜单 | `打开 DesktopAgent` | `打开 Tessel` |
| First-run / About / installer | DesktopAgent | Tessel |
| README / public docs | DesktopAgent product | Tessel product |

暂时不因为品牌改名而迁移：

```text
repository:               t1mb2rg/DesktopAgent
solution:                 DesktopAgent.sln
assemblies / namespaces:  DesktopAgent.*
source paths:              src/DesktopAgent.*
settings / database paths: existing DesktopAgent engineering paths
Diagnostics executable:   DesktopAgent.Diagnostics.exe
```

内部迁移以后如果有真实工程收益，可以单独立项，不和产品品牌切换绑在一起。

## 3. 顶层导航

当前导航结构可以保留：

```text
概览
AI 助手
待确认
布局
设置
```

这些名字已经足够短，不需要为了“品牌感”重新造词。

未来如果规则管理真正形成独立高频工作流，再讨论是否增加“规则”；在此之前不要把内部 `RuleSet` 概念硬抬成一级导航。

## 4. 概览页

当前概览仍是 M5 产品壳早期状态页，后续会按 `Tessel-product-direction.md` 重做，因此这里先冻结语言方向，不要求在品牌迁移时一次性重建布局。

建议：

| 当前 | 目标方向 |
|---|---|
| `DesktopAgent` | `Tessel` |
| `Assisted` | `辅助模式` |
| `Manual` | `手动模式` |
| `Automatic` | `自动模式` |
| `桌面执行 / 安全预览` | 随新 Overview 信息架构移除或改成真实状态卡 |
| `当前版本只观察、预览和保存布局。` | 在需要的位置直接说明“预览不会移动桌面图标” |

概览以后优先显示：当前桌面 / 布局可视化、当前布局、自动化状态、待确认数量、最近活动和快速操作。

不要为了概览数据显示完整实例化 Layout 工作台或昂贵 Shell 读取链。

## 5. 自动化文案

产品 UI 使用：

```text
自动化方式
```

替代主界面的：

```text
Automation Mode
```

三种方式：

```text
手动 · 只执行你当前明确要求的操作
辅助 · 你确认过的长期规则可以自动执行（默认）
自动 · 已确认规则和可靠的本地判断可以自动执行
```

原文：

```text
Automatic 仍不会让 AI-only 判断直接执行。
```

目标：

```text
即使选择“自动”，AI 的判断也不会单独触发桌面操作。
```

`Manual` / `Assisted` / `Automatic` 继续作为代码 enum 名，不需要为了中文 UI 改领域合同。

## 6. AI 设置

正式 UI 建议使用：

| 当前 | 目标 |
|---|---|
| `AI Provider` | `AI 服务` |
| `Base URI` | `服务地址` |
| `Model` | `模型` |
| `API Key（可选）` | 保留 |
| `尚未读取 Secret 状态。` | `正在检查 API Key…` |
| `API Key 只保存在 Windows DPAPI Secret Store。` | `API Key 只保存在这台电脑上，并使用 Windows 加密保护。` |

Secret 状态：

当前：

```text
API Key 已由 Windows DPAPI 安全保存。PasswordBox 不会回显现有 Key。
```

目标：

```text
API Key 已安全保存在这台电脑上。为了安全，已保存的 Key 不会再次显示。
```

当前：

```text
当前没有已保存的 API Key。本地无认证 Provider 可以保持此状态。
```

目标：

```text
当前没有保存 API Key。如果你的 AI 服务不需要认证，可以保持为空。
```

删除 Key 后当前：

```text
settings.json 只保留非秘密 Provider 配置。
```

目标：

```text
API Key 已从这台电脑中移除，其他 AI 服务设置会继续保留。
```

校验错误也使用产品语言：

```text
请选择有效的 Automation Mode。
→ 请选择一种自动化方式。

AI Base URI 不是有效的绝对 URI。
→ AI 服务地址格式不正确。

填写 API Key 前请先配置 AI Base URI 与 Model。
→ 填写 API Key 前，请先设置 AI 服务地址和模型。
```

设置保存成功文案：

```text
设置已保存
保存设置不会发起 AI 请求，也不会触发桌面移动。
```

这句可以保留，信息明确。

## 7. AI 助手页

当前占位页里的主要工程词：

```text
AI 生成的方案会成为 LayoutDraft，继续在布局工作台里可视化调整。
```

目标：

```text
AI 生成的方案会先变成可编辑的布局草稿，你可以在保存或使用之前继续调整。
```

`LayoutDraft` 继续作为内部模型名。

AI 页的原则不是强调“AI 很强”，而是清楚表达：

```text
自然语言
→ 结构化方案
→ 用户可视调整
→ 正常安全链
```

AI 不因为入口更自然就获得额外执行权限。

## 8. 待确认页

正式 UI 不出现 `Pending Queue` / `Pending Store`。

建议替换：

```text
正在读取 Pending Queue…
→ 正在读取待确认项目…

Pending Queue 目前为空。
→ 现在没有需要你确认的项目。

Pending Queue 读取失败。
→ 无法读取待确认项目。

Pending Store 保持有界读取；后续产品页会增加分页与批量处理。
→ 当前只显示最早的 100 项。
```

现有空状态：

```text
现在没有待确认项目
需要你决定的东西会留在这里。
```

可以保留。

待确认卡片应该优先解释：

- 这是什么；
- Tessel 想做什么；
- 为什么需要确认；
- 确认一次和记住这个习惯有什么区别。

不要把授权依据 enum、Store 状态或 Writer payload 暴露成主文案。

## 9. 布局页

现有顶层语言整体已经接近目标：

```text
布局
看见空间，再决定怎么摆。
我的布局
桌面画布
编辑中 · 自动化暂停
预览
桌面线框
详细影响
```

这些可以继续保留。

需要处理的工程词：

### RuleSet 缺失

当前：

```text
布局存在，但 RuleSet 缺失。拒绝进入半状态编辑。
```

目标：

```text
这个布局的数据不完整，暂时无法编辑。可以尝试刷新列表；如果问题仍然存在，请使用 Diagnostics 检查数据。
```

### ZoneOverride

当前：

```text
当前布局包含 ZoneOverride 规则。为避免旧规则指向失效分区，本阶段不会自动替换这些 Zone。
```

目标：

```text
这个布局包含固定分区规则。直接替换分区可能让这些规则失效，请先调整或移除相关规则。
```

`ZoneOverride` 只留在工程详情 / Diagnostics。

### Observation

当前详细影响中直接显示：

```text
Observation <guid>
<timestamp>
```

主产品 UI 默认不需要展示 GUID。

目标：

```text
桌面状态读取于 13:24:18
```

如以后提供“技术详情”，可以在那里显示 ObservationId。

### 保存版本

当前：

```text
布局已保存
V3
```

目标：

```text
布局已保存
你的修改已经保存。
```

版本号保留在存储、Diagnostics 或技术详情，不作为普通保存反馈。

### 设备名

`\\.\DISPLAY1` 这类 DeviceName 可以继续存在于高级 / 固定设备绑定配置，但不应该成为普通用户理解布局的第一信息。

如果后续能拿到更自然的显示器名称，优先显示用户可识别名称，DeviceName 放在技术详情或括号补充中。

## 10. 桌面分区框设置

这部分现有语言整体已经比较自然，可以保留：

```text
桌面分区框
轻量 / 标准 / 明显
高级自定义
边框粗细
边框透明度
框内透明度
圆角
分区颜色
在桌面上预览
实时预览
```

现有：

```text
小预览实时变化；“在桌面上预览”只临时显示示例分区，不会移动图标。
```

可以保留。

M5-07.5 Desktop Zone Editing 实现后，需要严格区分：

```text
显示分区框
→ 只显示 / click-through

编辑分区
→ 进入 Layout Editing / Automation BLOCK
```

不要把“桌面线框”和“编辑分区”做成同一个模糊开关。

## 11. Tray

目标用户可见菜单：

```text
打开 Tessel
────────
布局
待确认
设置
────────
退出
```

Tray Tooltip：

```text
Tessel
```

后续 Resident Lifecycle Optimization 可以在打开 tray menu 时开始非阻塞 Interactive prewarm，但这个内部优化不需要增加任何用户文案。

## 12. Diagnostics

产品品牌迁移第一阶段仍保留真实工程身份：

```text
DesktopAgent Diagnostics Workbench
DesktopAgent.Diagnostics.exe
```

README 可以称它为 Diagnostics / Diagnostics Workbench，并明确它不是 Tessel 日常产品 UI。

如果以后确实决定改为 `Tessel Diagnostics`，应作为独立品牌迁移步骤，同时检查：

- AssemblyName / Product metadata；
- 窗口标题；
- acceptance contract tests；
- docs / screenshots；
- installer / Start Menu identity。

不在纯文案阶段提前制造二次工程身份迁移。

## 13. About / First-run / Installer

这几块在 Packaging 前统一完成。

建议身份：

```text
Tessel
Windows 11 desktop organizer
```

中文产品一句话：

```text
Tessel 是一个 Windows 11 桌面整理工具。
```

First-run 重点只解释：

- 自动化方式；
- AI 可以不开；
- Tessel 不会因为一次 AI 判断直接移动桌面；
- 关闭窗口后会留在托盘；
- 如何真正退出。

Installer / Start Menu / Uninstall display name 使用 `Tessel`，内部工程路径不需要暴露。

## 14. 实施顺序

品牌和文案迁移建议分批落地：

```text
1. Window title / Overview title / Tray identity
2. Settings terminology
3. Pending terminology
4. AI placeholder copy
5. Layout engineering-copy cleanup
6. First-run / About
7. Packaging identity
8. Optional Diagnostics branding decision
```

每一批只改表现层字符串时，不应改变领域 enum、持久化格式、Automation Policy 或 Writer safety semantics。

## 15. 完成检查

正式 Tessel Surface 完成文案迁移时检查：

- 用户可见位置不再把 `DesktopAgent` 当作产品品牌；
- 主 UI 不直接出现 `LayoutDraft`、`Pending Store`、`Pending Queue`、`RuleSet`、`ZoneOverride`、`AI-only`、`Observation <guid>`；
- `Manual` / `Assisted` / `Automatic` 以用户语言显示；
- DPAPI 可以继续作为实现，但普通设置页解释为 Windows 加密保护；
- 所有“预览”明确不会移动桌面图标；
- 所有“使用布局 / 当前布局”明确是偏好还是实际执行，不混淆；
- Desktop Zone Editing 仍然遵守 Layout Editing = Automation BLOCK；
- Diagnostics 保留足够工程信息，不为了产品文案失去可诊断性；
- 未实现能力不出现在“现在能做什么”里。
