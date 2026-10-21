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

## 发布结论

- **当前判定：HOLD。** 当前文件已做首批匿名化，但完整历史审计和资产检查尚未完成。
- **不得误报**：Gitleaks 通过不代表可以公开；它只覆盖历史中的常见秘密匹配，不涵盖隐私/版权/二进制与线上资产。
- **不做**：不在本 PR 转 Public，不在本 PR 重写 Git 历史，不随意删除有价值的实验技术事实。
