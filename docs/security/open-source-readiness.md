# Tessel 1.0.1 开源公开前验收

状态：**仓库已于 2026-10-22 转为 Public。** 本文件保留公开前的风险裁决与审计证据，末尾补充了实际执行结果。

> 下文第 3 行的原始状态、"发布结论 HOLD"以及若干未勾选条目，记录的是**公开决策之前**的时点。
> 实际放行依据与完成情况见文末「## 实际公开执行记录（2026-10-22）」。

范围：从 `main` 建立 `chore/open-source-readiness`；目标是保留可复现的工程证据，识别真正需要处理的秘密、私人内容和授权风险，而不是消除普通开发环境痕迹。

## 公开标准裁决：风险导向，默认保留历史

维护者已确认 Tessel 计划开源，并认可：**普通开发路径、Windows 用户目录、非敏感软件名、内网地址以及真实坐标、显示器参数等工程记录可公开**。它们可能泄露少量环境信息，但不自动构成阻断项。不因 Gitleaks 之外的宽泛模式命中就停止发布，也不以“Git 历史完全匿名”为验收目标。

**必须调查或处理**：真实 API Key / Token / 私钥 / 认证配置；明显涉及私人事务或第三方个人资料的文件名/正文；不应公开的内部资料或研究内容；受限素材、许可证问题；误打包的本地账户数据、日志及秘密。

**默认保留仓库与 Git 历史，不计划重写 SHA 或迁移新仓库。** 只有实际发现严重敏感信息且无法接受既有历史可见性时，才单独讨论历史清理，提前评估旧 PR、tag、Release、fork 与历史引用影响。

公开前审计以**针对高风险内容的抽样和处置**为主，普通路径候选匹配可直接归类为“允许的开发痕迹”，无需逐条审核并签字。

## 已完成（当前工作树，不代表 Git 历史已脱敏）

- `docs/experiments/explorer-grid-physical-probe-v1.md`：将两项真实桌面 StableId 替换为匿名示例；保留 Probe 执行记录、物理落点、错误分类和恢复统计。
- `docs/experiments/explorer-grid-measurement-fact-v1.md`：以 `example-user` 代替本机 Windows 用户目录。
- `docs/experiments/M1-05C-B-controlled-target-display-restore-poc.md`：匿名化 StableId 中的用户目录。
- `docs/experiments/2026-10-19-feedback-fixes.md`：将真实桌面应用名称替换为“示例桌面快捷方式”；保留移动数量和位置变化诊断。
- 根目录已有 GPL-3.0-or-later `LICENSE`、`src/DesktopAgent.WinUI/ThirdPartyNotices.txt` 与常见本地数据的 `.gitignore`。

**重要：上述修改只改变当前文档，不会改变旧提交、旧分支、PR diff、Release 或 Actions 日志。** 已决定允许普通开发环境信息公开，因此不必为这类内容重写历史；如果发现实质敏感内容，仍须针对性处置。

## 发布硬门：可执行的全历史 Gitleaks 扫描

`.github/workflows/open-source-secret-audit.yml` 会在本 PR 触发后：

1. Checkout 完整 Git 历史，补齐 remote branch refs 与 tags（包含合并前的开发分支）。
2. 运行固定版本的 Gitleaks，对 `git log --all` 可达历史做扫描，**不是只检查 PR 差异**。
3. 日志只报告通过或阻断，不打印匹配片段，也不上传扫描报告附件。

本地复核（Windows PowerShell，在仓库目录，需先自行安装 `gitleaks` 可执行文件）：

```powershell
.\tools\security\invoke-secret-audit.ps1 -FetchAllRefs
```

本地扫描结果放在已由 Git 忽略的 `artifacts/open-source-audit` 目录。发现匹配时，逐项判断误报、测试假值或真实凭据。真实凭据必须**先撤销/轮换**，不能仅删当前文件。对误报做明确记录，不要为“全绿”使用宽泛 allowlist。

注意：`--all` 包含当前本地可达分支与 tag 的提交，不涵盖已删除或不可达对象、独立 fork、GitHub Actions 日志/附件、PR 评论及图片中的秘密。它也不能发现所有个人信息。

## 公开前人工检查清单（全部完成才可放行）

- [x] 本 PR 普通 CI 已通过：最新已核实的 build #880（Debug/Release/全量测试）结果为 SUCCESS；后续新提交需要重新核对对应运行。
- [x] 全 refs Gitleaks 初次验证通过（workflow `open-source-secret-audit` #2 / run `39627140119`，8.30.1，获取 111 个 refs，0 条检测匹配）；以后更新或公开前需重新运行。
- [ ] 对历史盘点进行**风险分级抽查**：优先人工检查疑似个人/第三方私人文件名、非测试凭据和真实私人资料；普通用户路径、测试数据与内网地址可归为允许公开，不要求逐条清零。
- [ ] README 四张截图已目视初审：**保留真实产品界面优先**，只确认未出现不愿公开的私人会话内容、第三方资料或密钥；仅对确实敏感的区域定向替换/遮挡，不强制全部换成合成截图。
- [ ] 检查 Release 下载包、安装器内容、发布日志和可下载 artifacts，无打包本机配置或秘密。
- [ ] 检查历史 PR 的描述、评论、截图、附件及 Actions 日志的隐私内容；尤其是曾记录真机证据的讨论。
- [ ] 核对源码版权归属、第三方依赖许可证，以及分发时的第三方声明和素材使用权。
- [x] **历史策略：默认完整保留**。不因普通路径、显示器参数或技术日志重写历史；如未来发现实质敏感内容，另行评估备份和定向清理方案。
- [ ] 管理员确认公开范围和风险，才将仓库改为 Public。
- [ ] 公开后立即给 `main` 启用有效的 Ruleset：PR 必需、CI 必需、禁 force push / deletion，随后确认实际生效。

## README 截图初审（仅视觉初审，不等于二进制全面检查）

已逐张打开现有 `docs/images/*.png` 的四张 README 产品截图：

- `ai-assistant.png`：出现了实际对话和工作/分区名称。**不要求全部替换为合成截图**，仅由维护者确认这些具体内容愿意公开；如有私人事务或第三方信息，定向遮挡或改成演示内容。仍须检查图片元数据。
- `desktop-overview.png` / `design-library.png`：主要是桌面项目数量、分区及设计名称。保留真实布局的复现价值，只需维护者确认其中没有不愿公开的项目、账户或第三方内容。
- `settings.png`：显示公开 AI Provider 服务地址和模型名，Key 输入框未暴露明文；可作为功能示意，仍需检查图像元数据。

**范围限制**：以上仅是当前 README 四张图片的目视检查。仓库其他二进制附件、历史版本图片、工作流 artifacts、Release 资产都尚未审计。

## 第二轮审计：历史候选、1.0.1 发布源码、抽样日志

### 全 refs 历史隐私盘点（2026-10-21）

本分支新增 `tools/security/history_privacy_inventory.py`，并已在 workflow `open-source-secret-audit` #6 / run `39630581636` 成功运行。读取 `git log --all` 可达的历史文本补丁（含合并时第一父链的差异），只向 CI 输出类别计数，不输出命中原文、文件名或提交号。详情写入 Runner 临时的本地 JSON，**不上传、不会长期保留**；维护者必须在自己的完整 clone 中复跑以逐项查看位置。

| 检查类别 | 历史补丁候选匹配次数 |
| --- | ---: |
| Windows 用户目录 | 65 |
| StableId 中的桌面身份/文件扩展名 | 32 |
| RFC1918 内网地址形式 | 55 |
| 邮箱地址形式 | 4 |

扫描约 378364 行变更文本。计数包含历史版本、增删两边、测试样例、重复出现以及误报；**不能等同于唯一隐私数据的个数**。真实私人文件标题已在原始提交中确认存在，因此旧历史不能宣称净化。

本地运行：

```powershell
python tools/security/history_privacy_inventory.py
# 私有报告：artifacts/open-source-audit/history-privacy-inventory.json
```

**历史保留已裁决**：默认选择 **保留既有仓库和全部历史**。其余两种方案（新建公开快照仓库 / 定向重写 Git 历史）不作为普通路径或测试信息命中的默认处置；仅在确认存在不能公开的真实敏感信息时重新评估。

### 1.0.1 发布源码的静态检查

- 已确认 `main` 包含 1.0.1 产品图标与版本更新（`357336e1`、`4e1f4b5b`），存在 `v1.0.1` tag。PR #112 仍以公开前脱敏为目的，不应修改正式版本行为。
- `src/DesktopAgent.WinUI/DesktopAgent.WinUI.csproj` 使用 `ApplicationIcon` 嵌入 `tessel.ico`；显式声明随发布复制的文本是 `ThirdPartyNotices.txt`，未发现显式将本地数据库、DPAPI secret 或 `.env` 复制到发布目录的项目设置。
- `release.yml` 使用 `dotnet publish` 生成自包含目录并 zip，Inno Setup `[Files]` 递归打包该发布目录。因此**必须检查产出的 zip 实体内容**，单凭 csproj 的静态结构不能证明最终包里没有配置文件。
- 发行脚本的 `Upload test diagnostics` 会在 CI 中保留短期 TRX/诊断资产。需要检查旧发行运行及公开后历史日志的暴露范围，不能假设 private 状态延续。

**待办：** 下载并展开 1.0.0、1.0.1 Release ZIP 与安装器，审查实际文件清单、意外本机配置、第三方许可和可执行资源；保留 SHA256 与经过脱敏的审核记录。现已提供本地 ZIP 初筛工具（需要自己下载真实发布包）：

```powershell
python tools/security/release_zip_audit.py "C:\path\to\Tessel-1.0.1-win-x64.zip"
# 私有报告：artifacts/open-source-audit/release-zip-audit.json
```

它检查可疑数据库/密钥/运行时配置文件、压缩包路径、安全敏感文本和 PDB 标记。**不会检查 Inno Setup 安装器内部及编译后 DLL/EXE 的所有二进制字符串或 PE 元数据，也不能替代人工审阅**。不能在 CI 没有真实 ZIP 的情况下将此任务勾选为完成。

### PR 与 Actions 抽样

已对 30 条近期 PR 描述执行了常见本机路径/文件名模式初筛，对三次已知构建运行的日志执行了相同关键词的只计数初筛，未检测到对应模式；**不是**完整的历史 PR 评论、附件或全部 Actions 日志审计。

## 收口确认：风险导向验收（2026-10-22）

- 维护者已同意保留现有真实工程历史，不再以普通本地路径、软件名称、坐标、RFC1918 内网地址为单独阻断项。
- 审计分支 CI #880 已通过（提交 `f2c2b4d2`），同一提交的安全审计 #11 已通过。
- 正式发布包检查仍需**下载真实发行 ZIP / 安装器**并执行 `tools/security/release_zip_audit.py`，不能用脚本自测或静态查看打包配置代替实际资产核查。
- GitHub Releases 下载资产不是普通 Actions test-diagnostics artifact；审查应针对 `v1.0.1` 与 `v1.0.0` 真正发布的 ZIP/EXE。扫描输出保存为本地非公开记录。
- README 截图优先保留真实界面，只排查真实私人对话、第三方材料、凭据，不要求全面制作模拟截图。
- 公开权限仍由维护者最后确认；在最终许可前，PR 保持 Draft，仓库保持 Private。

## 发布结论

- **当前判定：HOLD（范围已大幅缩小）**。不再因普通路径和历史痕迹阻断。剩余发布物实检、针对性敏感内容核查、第三方许可/素材以及管理员最终公开确认仍未完成。
- **不得误报**：Gitleaks 通过只代表未检测到其规则覆盖的秘密；历史隐私候选行数并不等于泄漏数量，也不是清零目标。
- **不做**：不在本 PR 转 Public，不在本 PR 重写 Git 历史，不随意删除有价值的实验技术事实。

## 实际公开执行记录（2026-10-22）

公开决策：维护者确认保留既有历史、只处置真实敏感内容，并授权直接转为 Public。

### 已完成

| 项 | 结果 |
| --- | --- |
| 仓库可见性 | `main` 分支（提交 `a54224c`）转 Public，匿名访问返回 HTTP 200；GitHub 许可证识别为 `GPL-3.0` |
| 仓库描述与检索 | 重写 description，补齐 10 个 topics（`windows` `windows-11` `winui3` `dotnet` `csharp` `desktop-customization` `ai` `desktop-automation` `llm` `gplv3`） |
| 历史遗留 PR | 关闭 81 个已并库的 Draft PR，仅保留本审计 PR |
| 密钥扫描 | Secret scanning、Push protection、Dependabot security updates 已启用（`non-provider patterns` / `validity checks` 属付费项，免费仓库无法开启） |
| 当前文档脱敏 | 5 处 `C:\Users\<user>\`、1 处真实桌面文件名（提交 `2f5a3eb`）；`git grep 29719 docs/` 为空 |
| 第三方许可 | 新增 `THIRD-PARTY-NOTICES.md`，逐个从 `.nuspec` 读取许可证；确认 **Windows App SDK 是 Microsoft 专有 EULA（非 MIT）**，源码 GPL-3.0 与运行时的关系属 GPL 第 5 条聚合体（提交 `a54224c`） |
| 许可文本随包分发 | `tools/publish.ps1` 复制 `LICENSE.txt` / `THIRD-PARTY-NOTICES.md` / `ThirdPartyNotices.txt` / `CHANGELOG.md` 进发布目录，缺失即构建失败 |
| 发布包实检 | `tools/security/release_zip_audit.py` 审 `Tessel-1.0.1-win-x64.zip`：500 成员、0 flags、0 未扫描大文本 |

### 实检发现并修复：发布包泄露构建机路径

首次审计报告 **5 个 flag**，全部是 `.pdb`：

```text
DesktopAgent.Application.pdb / Core.pdb / Persistence.pdb / WindowsInterop.pdb / WinUI.pdb
```

`DesktopAgent.WinUI.pdb` 在 offset 100324 处含有：

```text
C:\Users\<user>\.nuget\packages\microsoft.windowsappsdk.foundation.2.3.5\include\...
```

即调试符号把**构建机的绝对路径（含 Windows 用户名）**带进了发布包。已修复：

- `tools/publish.ps1` 与 `.github/workflows/release.yml` 均加 `-p:DebugType=none -p:DebugSymbols=false`；
- 发布步骤额外删除任何残留 `.pdb`，仍有残留则直接失败（不静默放过）。

修复后重打包：500 文件 / 183.3 MB，`release_zip_audit.py` **0 flags**，`pdb 条目 = 0`。全包文本扫描仅 `Microsoft.WinUI.dll` 命中一次 `29719`，定位为哈希串 `667D265297196BFC` 的巧合子串，非泄漏。

### 仍未处理

- **已发布的 v1.0.0 / v1.0.1 Release 资产仍含 `.pdb`**（修复只影响后续版本）。泄露内容限于本机 Windows 用户名与 NuGet 缓存路径，不含凭据、源码路径或个人文件；是否重发资产由维护者决定。
- `Tessel-Setup-1.0.1.exe` 安装器内部未做二进制级审计（工具限制：只覆盖 zip 成员与小文本）。
- 历史提交中的 Windows 用户名保留（重写会改变全部 1099 个提交 SHA，使 `v1.0.0` / `v1.0.1` tag 与已发布安装包失联）。

### 判定

公开条件成立，依据是：真实凭据零命中（Gitleaks 全 refs 多次通过 + 本次人工复核）、真实敏感内容已处置、许可与第三方声明完整并随包分发、发布包可执行文件不含构建环境信息。
