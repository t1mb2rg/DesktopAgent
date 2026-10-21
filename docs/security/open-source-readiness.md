# Tessel 1.0.0 开源公开前验收

状态：**进行中 / 不允许据此直接将 GitHub 仓库改为 Public**。

范围：从 2026-10-21 的 `main` 建立 `chore/open-source-readiness`；目标是保留可复现的工程证据，同时不把个人 Windows 桌面和机器信息作为开源项目的测试数据公开。

## 已完成（当前工作树，不代表 Git 历史已脱敏）

- `docs/experiments/explorer-grid-physical-probe-v1.md`：将两项真实桌面 StableId 替换为匿名示例；保留 Probe 执行记录、物理落点、错误分类和恢复统计。
- `docs/experiments/explorer-grid-measurement-fact-v1.md`：以 `example-user` 代替本机 Windows 用户目录。
- `docs/experiments/M1-05C-B-controlled-target-display-restore-poc.md`：匿名化 StableId 中的用户目录。
- `docs/experiments/2026-10-19-feedback-fixes.md`：将真实桌面应用名称替换为“示例桌面快捷方式”；保留移动数量和位置变化诊断。
- 根目录已有 GPL-3.0-or-later `LICENSE`、`src/DesktopAgent.WinUI/ThirdPartyNotices.txt` 与常见本地数据的 `.gitignore`。

**重要：上述修改不会删除旧提交、旧分支、GitHub PR diff、Release 快照或 Actions 日志中的原文。**
即使之后合并本 PR，已有历史引用仍然可被访问。需要在公开前单独做历史风险裁决，不能把“当前文件看起来干净”当作历史已净化。

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

- [ ] 本 PR 普通 CI（Debug/Release/全量测试）通过。
- [x] 全 refs Gitleaks 初次验证通过（workflow `open-source-secret-audit` #2 / run `39627140119`，8.30.1，获取 111 个 refs，0 条检测匹配）；以后更新或公开前需重新运行。
- [ ] 按 `git log --all -p`、全仓路径/邮箱/域名扫描抽检历史提交中的私人路径、用户桌面文件名、敏感日志。当前工作树已脱敏的内容仍然存在历史里，需要裁决保留、定向历史清理或重新公开发布源快照。
- [ ] README 四张产品截图已逐张目视初审，尚需决定是否替换真实会话内容并出具可公开的演示截图（见下文）。
- [ ] 检查 Release 下载包、安装器内容、发布日志和可下载 artifacts，无打包本机配置或秘密。
- [ ] 检查历史 PR 的描述、评论、截图、附件及 Actions 日志的隐私内容；尤其是曾记录真机证据的讨论。
- [ ] 核对源码版权归属、第三方依赖许可证，以及分发时的第三方声明和素材使用权。
- [ ] 明确旧 Git 历史与分支的公开策略；如必须重写历史，先备份并单独讨论 tags、PR 引用、Release 资产与协作者影响。
- [ ] 管理员确认公开范围和风险，才将仓库改为 Public。
- [ ] 公开后立即给 `main` 启用有效的 Ruleset：PR 必需、CI 必需、禁 force push / deletion，随后确认实际生效。

## README 截图初审（仅视觉初审，不等于二进制全面检查）

已逐张打开现有 `docs/images/*.png` 的四张 README 产品截图：

- `ai-assistant.png`：出现了实际的会话消息和一些工作/分区名称；虽无明显密钥，但这些内容不是保证匿名的演示数据。**公开前建议改成合成的示例会话截图**，并检查图片元数据。
- `desktop-overview.png` / `design-library.png`：以桌面项目数量、分区与工作区设计名称为主，未见明显帐号或密钥；但这些是实际布局快照，需要维护者确认愿意公开。
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

**历史保留裁决（必须由维护者作出）**：

- **方案 A：保留既有仓库和全部历史**。仅在逐项确认历史中的真实私人桌面文件名、用户名/路径、附件等都可以公开时成立；当前不能默认选择。
- **方案 B：原私人仓库保留完整工程历史，创建新的公共源码快照仓库**。可保留技术遗产私有可查，但会改变原仓库 URL、Issues/PR 关联和发布流程，需要迁移设计；当前也不得自动实施。
- **方案 C：对既有仓库定向清洗 Git 历史**。影响所有被改写的 commit SHA、tag 和老 PR 比较面，并不能自动清除已存在的 GitHub PR 评论、Actions 日志或下载资产；须事前备份及独立迁移计划，不能由普通脱敏 PR 顺带执行。

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

## 发布结论

- **当前判定：HOLD。** 当前文件已做首批匿名化，但完整历史审计和资产检查尚未完成。
- **不得误报**：Gitleaks 通过不代表可以公开；它只覆盖历史中的常见秘密匹配，不涵盖隐私/版权/二进制与线上资产。
- **不做**：不在本 PR 转 Public，不在本 PR 重写 Git 历史，不随意删除有价值的实验技术事实。
