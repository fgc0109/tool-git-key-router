# GitKeyRouter 项目优化审计与路线图

> 本页保留 2026-07-26、基线 `226b875` 的历史审计结论。当前完成状态、v0.4.5–v0.4.9 增量和重新规划请以[最新优化状态与后续规划](project-optimization-status.md)为准。

- 审计日期：2026-07-26
- 代码基线：`226b8755d75b98b0c50bc03a549f921656d94bcc`

## 审计范围

本次检查覆盖版本配置与历史、Core/Infrastructure 关键事务路径、备份、日志脱敏、子进程执行、测试项目、CI、Release 工作流及现有文档。建议只记录当前代码或工具结果能够支持的事实；没有执行过的检查不写成已通过。

## 当前基础

项目已经具备较好的可靠性基础：

- 使用 .NET 10、锁定恢复、确定性构建和 CI 警告即错误。
- CI 已执行格式检查、Release 构建和完整测试，并在失败时上传 TRX。
- 当前依赖审计未发现已知漏洞或可更新包。
- Git Profile、SSH 预览和 Git URL Rewrite 已增加事务、冲突检查或自动回滚。
- 备份目录已经采用 `.pending-*` 准备后原子发布。
- ProcessRunner 已限制行数和单行长度，并暴露截断与终止失败诊断。

下面的项目是在这些基础上继续提高崩溃恢复、可维护性和供应链可信度。

## P0：优先处理

### 1. 为 Git Profile 事务增加持久化恢复日志

**已验证现状**

`GitProfileService` 会在内存中捕获相关文件和全局 `include.path`，失败时能够在同一进程内回滚。但通用 `BackupService` 持久化的内容仍只有应用配置、SSH config 和 Git URL rewrite，不包含 `profiles.gitconfig`、`profile-*.gitconfig` 或原 `include.path`。

**剩余风险**

进程崩溃、断电或主机重启发生在正式文件替换之后、回滚完成之前时，内存快照会丢失，无法依赖现有备份自动恢复。

**建议验收标准**

- 应用前在专用事务目录持久化所有受影响文件、存在状态、SHA-256 和原 `include.path` 精确序列。
- 事务目录具有 `prepared / applying / committed / rolled-back` 等可恢复状态。
- 启动时检测未完成事务，并允许自动恢复或在 UI 中明确提示。
- 完成或成功回滚后安全清理事务目录。
- 增加在每个写入阶段模拟异常终止的恢复测试，而不仅是方法内异常测试。

建议提交：`fix(profiles): persist recoverable application transactions`

### 2. 完成 v0.5.0 的正式版本一致性

**已验证现状**

v0.5.0 两阶段实现与 159 项测试已经完成，但 `Directory.Build.props` 仍是 `0.4.4`，仓库没有 `.github/releases/v0.5.0.md`，当前记录也不能证明标签和发布产物存在。

**建议验收标准**

- 将 Version、AssemblyVersion 和 FileVersion 一致提升到 0.5.0。
- 新增正式发布说明，内容与 `docs/v0.5.0.md` 和实际提交一致。
- 完成锁定恢复、格式、完整测试、Release 构建和两种 Windows x64 发布冒烟测试。
- 检查 ZIP、EXE 和 `SHA256SUMS.txt`，再创建并回读验证标签与发布资产。

建议提交：`chore(release): prepare GitKeyRouter v0.5.0`

### 3. 将预览冲突令牌扩展到应用配置和 rewrite 计划

**已验证现状**

SSH 与 Git Profile 预览记录文件存在状态和 SHA-256。当前未发现应用配置预览或 Git URL rewrite 计划携带等价的版本令牌；rewrite 应用能够在执行失败时回滚，但不能证明用户确认前后计划依据的当前值没有被外部修改。

**建议验收标准**

- 应用配置预览记录配置文件的存在状态和 SHA-256。
- rewrite 计划记录所有涉及 key 的原始精确值集合及规范化哈希。
- 应用前重新读取并比较；不一致时拒绝执行并要求重新生成预览。
- 计划外 Git 配置始终不参与比较、写入或回滚。
- 覆盖外部编辑、值顺序变化、重复值和计划外变化测试。

建议提交：`fix(preview): protect config and rewrite plans from stale state`

## P1：可靠性和维护性

### 4. 显示并管理损坏或未完成的备份

**已验证现状**

`BackupService.ListAsync` 会忽略 `.pending-*`，并跳过无法读取或验证的备份目录。这避免把损坏目录当成可恢复快照，但用户也无法看到异常目录或安全清理它们。

**建议验收标准**

- 将目录分类为完整、未完成、损坏和未知版本。
- 显示失败原因、可用文件和完整性状态。
- 只允许清理经过路径约束且不属于活动事务的异常目录。
- 清理前预览，清理后重新扫描验证。

建议提交：`feat(backup): surface and clean incomplete snapshots safely`

### 5. 扩展敏感信息脱敏

**已验证现状**

`SensitiveDataRedactor` 当前主要处理 PEM/OpenSSH 私钥块，现有测试覆盖私钥正文。尚未看到 URL 凭据、Bearer、常见 secret 赋值、ASKPASS 或常见托管平台 Token 的系统性规则。

**建议验收标准**

- 覆盖带用户名密码或 Token 的 URL、`Authorization: Bearer`、`password/token/secret=`。
- 覆盖 `GIT_ASKPASS`、`SSH_ASKPASS` 和常见 GitHub/GitLab Token 外形。
- 保留普通路径、SSH 公钥、指纹、提交 SHA 和非敏感 URL。
- 使用表格驱动测试同时覆盖应脱敏与不应脱敏样本。
- 对每条规则设置长度和回溯约束，避免日志输入造成正则性能问题。

建议提交：`fix(logging): redact common credential formats`

### 6. 迁移到 xUnit v3 并集中管理测试依赖

**已验证现状**

依赖审计没有发现漏洞或可更新包，但两个测试项目使用的 `xunit 2.9.3` 被标记为 Legacy；测试依赖版本在两个 csproj 中重复维护。

**建议验收标准**

- 按官方迁移路径切换到 xUnit v3，并更新 runner、Test SDK 和锁文件。
- 保持全部 159 项现有测试语义，迁移后重新执行完整测试和 Release 构建。
- 引入中央包管理或等价的单一版本来源，避免两个测试项目漂移。
- CI 中保留 TRX 或提供等价的失败诊断产物。

建议提交：`test: migrate the test suite to xUnit v3`

### 7. 让 ProcessRunner 测试完全确定化

**已验证现状**

ProcessRunner 的部分测试直接调用 `cmd.exe`、`ping` 和 100ms 级超时。它们验证了真实 Windows 行为，但容易受机器负载、网络工具差异和进程调度影响。

**建议验收标准**

- 增加专用测试子进程或测试模式，确定地产生 stdout、stderr、大行、挂起和子进程树。
- 超时/取消测试使用握手文件、命名管道或标准输入确认子进程已进入目标状态后再触发。
- 保留少量真实 Windows 冒烟测试，其余逻辑使用确定性输入。
- 连续或重复运行不出现偶发失败。

建议提交：`test(process): use a deterministic child process harness`

### 8. 同步架构、备份和故障排除文档

**已验证现状**

`docs/architecture.md` 尚未完整描述 Git Profile 持久化事务边界、严格/保守 SSH 同步和 rewrite 回滚；`docs/backup-and-restore.md` 未描述 pending 原子发布与完整性验证；`docs/troubleshooting.md` 未说明输出截断、`KillFailed` 和 `TerminationError`。

**建议验收标准**

- 架构文档与当前服务边界、事务顺序和失败语义一致。
- 备份文档解释目录状态、manifest、SHA-256 和恢复限制。
- 故障排除文档解释启动失败、取消、超时、截断和终止失败。
- 新增严格同步 UI 文案全部进入现有中英文资源或本地化入口，并进行双语言检查。

建议提交：`docs: align reliability and recovery documentation`

## P2：质量门禁和供应链

### 9. 增加核心代码覆盖率门禁

- 先生成 Core/Infrastructure 覆盖率报告并建立基线。
- 对事务、回滚、解析和配置迁移设置合理阈值；不要用 UI 代码拉低核心门禁的可操作性。
- PR 展示差异覆盖率，阈值逐步提高而不是一次设置过高。

建议提交：`ci: enforce core reliability coverage`

### 10. 启用 .NET 推荐分析器

- 明确启用推荐分析级别并记录例外。
- 先以报告模式清理存量，再在 CI 中提升为错误。
- 重点检查取消令牌传播、资源释放、正则性能、平台兼容和异步使用。

建议提交：`build: enable recommended .NET analyzers`

### 11. 加强 Release 前置校验

当前 Release 工作流会响应 `.github/releases/v*.md` 变化。建议在真正发布前额外验证：

- 发布说明版本与 `Directory.Build.props` 完全一致。
- 对应标签不存在或指向预期提交。
- 两种发布目录都包含正确的 `GitKeyRouter.exe`，并执行轻量 CLI 冒烟测试。
- 历史发布说明修订不会被误判为新版本发布。

建议提交：`ci(release): validate version and artifact consistency`

### 12. 增加供应链证明

- 为发布产物生成 SBOM。
- 为工作流生成构建来源证明或等价 provenance。
- 在具备密钥管理条件后增加 Authenticode 签名，并在发布阶段验证签名。
- 校验文件、SBOM、签名和来源证明都应与最终上传的精确字节一致。

建议按独立边界拆分：

1. `ci(release): generate SBOM and provenance`
2. `ci(release): sign and verify Windows executables`

## 建议实施顺序

### 第三阶段 A：崩溃与并发安全

1. Git Profile 持久化恢复日志。
2. 应用配置与 rewrite 计划冲突令牌。
3. 备份健康状态与安全清理。

### 第三阶段 B：安全和可维护性

1. 日志敏感信息脱敏。
2. 确定性 ProcessRunner 测试。
3. xUnit v3 与中央包管理。
4. 可靠性文档和本地化同步。

### 第三阶段 C：发布和供应链

1. 正式完成 v0.5.0 版本一致性。
2. 覆盖率和分析器门禁。
3. Release 版本/产物前置校验。
4. SBOM、来源证明和 Authenticode。

## 结论

项目当前主要功能和方法内失败恢复已经较完整。下一步最值得投入的不是继续增加普通功能，而是补齐**跨进程崩溃恢复、预览状态一致性、异常备份可见性和发布链路证明**。其中 Git Profile 持久化事务恢复应优先于其他优化。
