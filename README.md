# GitKeyRouter

[English](README.en.md) | **简体中文**

> **协作说明**
>
> GitKeyRouter 的大量架构设计、代码实现、测试、文档和发布流程由项目作者与 ChatGPT（OpenAI）协作完成。项目作者负责提出需求、确定产品方向、审阅与验收变更，并对最终设计决策、运行和发布负责。

GitKeyRouter 是一个面向 Windows 10 / Windows 11 的本地桌面工具，用于统一管理：

- GitHub.com、GitLab.com、自建 GitLab、Gitea 和通用 Git 服务实例
- 每个服务下的多个 SSH 身份、账号和密钥路径
- `%USERPROFILE%\.ssh\config` 中按服务生成的 HostAlias
- 各 Git 服务的默认身份回退路由，以及 Owner / Repository 精确路由的 `url.*.insteadOf` 全局 Git 配置
- 按目录或远程 URL 自动选择 `user.name`、`user.email` 和签名密钥的 Git Profiles
- 配置修改前的 diff、命令输出、备份和选择性恢复
- GUI 与 DevRunner 可调用的简单 CLI

项目使用 C#、.NET 10 和 WinForms，不使用数据库、WebView、Node.js、Electron、GitHub API、OAuth 或 PAT。

> 项目目标框架为 .NET 10，并在 Windows 环境通过 Release 构建和自动化测试验证。发布前仍建议在目标机器执行 `dotnet build --configuration Release` 和 `dotnet test --configuration Release`。

## 下载与版本选择

GitHub Releases 提供 Windows x64 发布包：

- **`GitKeyRouter-v<version>-win-x64-portable.zip`**：自包含便携版，内含 .NET 运行时，解压后可直接运行。
- **`GitKeyRouter-v<version>-win-x64-framework-dependent.zip`**：体积较小，需要目标机器安装 .NET 10 Desktop Runtime x64。
- **`SHA256SUMS.txt`**：两个 ZIP 包的 SHA-256 校验值。

从源码构建时需要 .NET 10 SDK；普通使用不需要 SDK。

## 安全和操作边界

GitKeyRouter 以“管理方便、状态透明、操作可恢复”为目标：

- 不实现 SSH 密钥算法，生成密钥时调用系统 `ssh-keygen.exe`
- Git 操作调用实际 `git.exe`
- SSH 测试调用实际 `ssh.exe`
- 不通过 `cmd.exe` 或 PowerShell 拼接 Git/SSH 命令
- 外部进程使用 `ProcessStartInfo.ArgumentList` 分别传递参数
- 不自动下载或安装 Git、OpenSSH 或其他软件
- 不保存、复制或显示私钥正文
- 删除身份记录时默认不删除密钥文件
- 自动管理 SSH Config 时只修改 GitKeyRouter managed block
- Git rewrite 使用 `git config --global` 精确读写，不覆盖整个 `.gitconfig`
- 危险操作先显示文本 diff 或结构化变更计划
- 修改前创建快照，恢复操作本身也会先创建新快照
- GUI 和带 `--yes` 的写入型 CLI 共享当前 Windows 用户的单实例锁；只读、预览、诊断、测试、版本和帮助命令可在 GUI 运行时使用
- 日志中的私钥块会被脱敏；日志默认每 5 MB 轮转并保留 3 份历史文件，日志写入失败不会中断主操作

## 系统要求

- Windows 10 或 Windows 11 x64
- .NET 10 SDK：仅源码构建需要
- Git for Windows：提供 `git.exe`
- Windows OpenSSH Client 或 Git for Windows OpenSSH：提供 `ssh.exe`、`ssh-keygen.exe`

程序启动和“一键诊断”会显示三个工具的：

- 是否存在
- 实际采用路径
- 其他候选路径
- 版本或文件版本
- 探测命令的 stdout、stderr 和 exit code

缺失时只显示明确提示，不会自动安装。

## 快速开始

### 1. 启动程序

无命令行参数时进入 WinForms：

```powershell
GitKeyRouter.exe
```

### 2. 配置 Git 服务和身份

GitHub.com 是不可删除的内置服务。使用 GitLab、Gitea 或其他自建服务时，先在“Git 服务”页面创建实例并填写域名、SSH 用户、端口和 Web Base URL。

然后打开“Git 身份”，创建例如：

```text
Git 服务: GitHub.com
显示名称: Camus GitHub
账号: camus0109
HostAlias: github-camus
私钥路径: C:\Users\fgc01\.ssh\id_ed25519_github_camus
公钥路径: C:\Users\fgc01\.ssh\id_ed25519_github_camus.pub
注释: camus0109
```

程序配置保存于：

```text
%APPDATA%\GitKeyRouter\config.json
```

密钥仍保存在用户选择的位置，不会复制到程序配置目录。

### 3. 生成或导入密钥

“生成密钥”调用：

```text
ssh-keygen.exe -t ed25519 -C <comment> -f <private-key-path> -N ""
```

第一版默认不设置 passphrase，界面会在执行前明确提示。

目标文件存在时可以：

- 取消
- 返回编辑身份并选择其他文件名
- 明确覆盖；新密钥会先在唯一临时路径生成并完成校验，然后旧密钥备份为 `.gitkeyrouter.<timestamp>.<unique>.bak`，最后才替换正式文件

`ssh-keygen` 失败、输出不完整、公钥无法解析、用户取消、正式文件替换失败，或原本不存在的目标在生成期间被其他程序创建时，程序不会覆盖新出现的文件；原密钥保持不变或从备份恢复，临时文件会被清理。生成成功后会显示完整公钥，并提供复制和导出功能。

GitKeyRouter 会识别同一身份目录中的多种公钥格式，并在“Git 身份”列表中为每个格式显示独立行：

- OpenSSH 公钥
- RFC4716 / SSH2 公钥
- PEM / PKCS8 公钥
- 未知或无效的候选公钥文件

格式转换不会覆盖原文件，而是使用明确的文件名并存：

```text
id_ed25519_account.pub             # 用户配置的原始公钥路径
id_ed25519_account.openssh.pub     # OpenSSH
id_ed25519_account.rfc4716.pub     # RFC4716 / SSH2
id_ed25519_account.pem.pub         # PEM / PKCS8
```

如果目标格式文件已存在，默认拒绝覆盖；用户明确允许覆盖时，会先创建 `.gitkeyrouter.<timestamp>.bak` 备份。备份和临时转换文件不会显示在公钥变体列表中。

重命名密钥文件时，程序会同时更新所有共享该文件的身份和对应的 SSH managed block。每个身份继续使用它所属 Git 服务的 `HostName`、SSH 端口和 SSH 用户；如果服务配置已缺失，预览会直接失败，不会把配置误写成 GitHub。

程序不显示私钥正文。选择已配置的 OpenSSH/PEM 私钥时，只会调用 `ssh-keygen -y` 派生新的 `.openssh.pub` 文件。PuTTY PPK 需要先用 PuTTYgen 转换。

### 4. 添加公钥到 Git 服务

在对应 Git 服务账户中打开 SSH Keys 页面，创建新 Key，并使用“复制公钥”复制标记为 OpenSSH 格式的公钥。RFC4716、PEM、私钥或结构损坏的文本不会被该按钮复制。

GitKeyRouter 不调用 GitHub、GitLab 或 Gitea API，也不会替用户上传公钥。

### 5. 同步 SSH Config

同步后只增加受控区块：

```sshconfig
# BEGIN GitKeyRouter managed block: github-camus
Host github-camus
    HostName github.com
    User git
    IdentityFile C:/Users/fgc01/.ssh/id_ed25519_github_camus
    IdentitiesOnly yes
# END GitKeyRouter managed block: github-camus
```

程序保留：

- 其他 Host 配置
- 用户注释
- 非受控文本
- 原有 CRLF/LF 换行风格

普通同步不会重写完整 SSH Config。只有用户主动进入“编辑原始文本”并确认完整 diff 时，才会执行完整文本替换。

### 6. 配置默认身份与仓库路由

所有 Git 服务都可以选择 `DefaultIdentityId`，GitKeyRouter 会为未命中更具体规则的仓库生成覆盖整个实例的服务级回退路由。Gitea 的 `AccountName` 只表示网页登录账号，不代表仓库 Owner。例如远程 Gitea：

```text
url.git@gitea-cloud:.insteadOf = git@git.policoil.top:
url.git@gitea-cloud:.insteadOf = ssh://git@git.policoil.top/
url.git@gitea-cloud:.insteadOf = git+ssh://git@git.policoil.top/
url.git@gitea-cloud:.insteadOf = https://git.policoil.top/
```

因此 `project-base/*`、`game-riki/*` 和 `game-hhmx/*` 都会保留原始 Owner，并统一使用 `gitea-cloud` HostAlias。两个独立 Gitea 服务可以共用密钥文件，但必须使用独立服务 Id、HostAlias 和 HostName。

GitHub 也可以配置默认身份，作为未配置 Owner / Repository 的回退；更长、更具体的 Owner / Repository 前缀仍优先匹配。若只希望显式多账号路由，可以不设置默认身份。示例：

```text
camus0109/*
→ github-camus

project-base-mirror/*
→ github-project-base
```

默认身份派生的服务路由统一使用 `service-default:<serviceId>` 标识。如果旧配置已有未标记的服务级路由，保存默认身份时会将其纳入托管；清除默认身份时会删除该派生路由，但不会删除 Owner / Repository 路由。

对于 `camus0109`，期望规则为：

```text
url.git@github-camus:camus0109/.insteadOf = https://github.com/camus0109/
url.git@github-camus:camus0109/.insteadOf = git@github.com:camus0109/
```

程序实际调用等价于：

```powershell
git config --global --add "url.git@github-camus:camus0109/.insteadOf" "https://github.com/camus0109/"
git config --global --add "url.git@github-camus:camus0109/.insteadOf" "git@github.com:camus0109/"
```

显示给用户的命令只用于复制和审阅；程序执行时不会把整条命令交给 shell。

## 仓库路由原理

输入：

```text
https://github.com/camus0109/panel-terraria.git
```

Git 根据 `insteadOf` 的最长匹配前缀，将它改写为：

```text
git@github-camus:camus0109/panel-terraria.git
```

OpenSSH 再根据 `Host github-camus` 选择对应私钥。

因此：

- Git 服务和 Owner / Namespace 决定使用哪个 HostAlias
- HostAlias 决定使用哪个 IdentityFile
- 仓库本身不需要逐个修改 SSH key 配置

## Git rewrite 状态

“Git 重写配置”页区分：

- `Correct`：精确规则存在一次
- `Missing`：没有任何对应前缀规则
- `Duplicate`：相同 Base URL 和 insteadOf 重复
- `Conflict`：同一个 insteadOf 前缀指向其他 Base URL
- `Extra`：当前 Git 中存在，但不属于启用仓库路由
- `LegacyAccountOwner`：旧版 Gitea“登录账号即仓库 Owner”路由，等待用户确认迁移

支持：

- 应用缺失配置
- 修复当前全部路由
- 删除指定规则
- 清理重复规则
- 复制对应 Git 命令

“修复”只处理当前启用路由涉及的精确前缀，不会自动删除其他无关 URL rewrite。

删除使用：

```text
git config --global --fixed-value --unset-all <key> <exact-value>
```

`--fixed-value` 用于避免把 URL 当作正则表达式删除。

## URL 测试

### 本地预览

同时读取当前 Git rewrite 和 GitKeyRouter 根据服务配置推导的期望 rewrite，并分别显示实际匹配、期望匹配、缺失/冲突状态和最终预期重写结果；预览不访问网络。

### 实际连接测试

用户明确确认后执行：

```text
git ls-remote <original-url> HEAD
```

结果窗口显示：

- 实际 executable
- 独立参数
- stdout
- stderr
- exit code
- 超时和耗时

## SSH 测试

普通模式：

```text
ssh -T git@github-camus
```

详细模式：

```text
ssh -vT git@github-camus
```

GitHub、GitLab 和 Gitea 的 SSH 测试可能在认证成功时仍返回非零 exit code，因此程序会由对应平台适配器检查 stdout 和 stderr 中的服务特定成功提示。

```text
successfully authenticated
```

原始输出始终可查看。

## 备份和恢复

备份目录：

```text
%APPDATA%\GitKeyRouter\backups\<timestamp>\
```

每个快照可能包含：

```text
manifest.json
app_config.json
ssh_config.txt
git_url_rewrites.json
```

其中：

- `app_config.json`：Git 服务、身份和仓库路由配置
- `ssh_config.txt`：修改前 SSH Config
- `git_url_rewrites.json`：修改前所有 `url.*.insteadOf` 规则
- `manifest.json`：时间、原因、配置 Schema、原文件是否存在、Git 快照是否成功等元数据

如果 Git 不可用，身份配置仍允许保存；快照会明确记录 Git rewrite 捕获失败，而不是伪造空快照。此类快照不能用于恢复 Git rewrite。

“备份与恢复”页面会扫描备份根目录中的每个直接子目录，并分类为完整、未完成、损坏、版本不支持或未知。完整备份会在列表阶段验证 manifest 与记录的文件 SHA-256。异常目录必须先生成清理预览并再次确认；完整备份、重解析点、活动或一小时内的 pending 目录不会被清理。

支持分别恢复：

- SSH Config
- Git URL rewrite
- 程序配置

恢复 Git rewrite 仍通过 `git config` 逐条精确删除和增加，不替换整个 `.gitconfig`。

## Git Profiles 与提交身份

0.3.0 新增“Git Profiles”页面。每个 Profile 可以保存 `user.name`、`user.email`、签名密钥、默认 Git 服务和默认 SSH 身份，并通过目录或远程 URL 规则自动生效。

目录规则生成 Git 官方的 `includeIf "gitdir/i:<目录>/"` 条件；远程 URL 规则生成 `includeIf "hasconfig:remote.*.url:<模式>"` 条件。程序不会逐仓库修改 `.git/config`，而是在 `%APPDATA%\GitKeyRouter\git-profiles` 中生成一个 managed 条件配置入口和独立 Profile 文件，并只在全局 Git 配置中注册一次 `include.path`。

“预览并应用”会先显示入口文件和所有 Profile 文件的 diff。删除 Profile 或规则后，需要再次应用才能同步删除已经生成的条件配置。

应用前会把所有受影响的 Profile 文件、原始存在状态、内容校验值和全局 `include.path` 顺序写入 `%APPDATA%\GitKeyRouter\git-profile-transactions`。如果进程在正式写入期间崩溃或系统断电，下一次取得排他锁的 GUI 或确认写入命令会先恢复原状态；恢复失败时会保留事务日志并阻止新的写入，避免覆盖仍可恢复的现场。

## CLI

GUI 与 CLI 使用同一个服务图和同一套业务逻辑。

```powershell
GitKeyRouter.exe diagnose
GitKeyRouter.exe list-services
GitKeyRouter.exe list-identities
GitKeyRouter.exe list-profiles
GitKeyRouter.exe list-routes
GitKeyRouter.exe apply
GitKeyRouter.exe apply --yes
GitKeyRouter.exe apply-profiles
GitKeyRouter.exe apply-profiles --yes
GitKeyRouter.exe parse-url ssh://git@gitlab.example:2222/company/platform/repo.git
GitKeyRouter.exe resolve-profile C:\code\work\repo --url https://gitlab.example/company/repo.git
GitKeyRouter.exe test-service gitlab-office
GitKeyRouter.exe test-route camus0109
GitKeyRouter.exe test-route company/platform --service gitlab-office
GitKeyRouter.exe test-route camus0109 --url https://github.com/camus0109/panel-terraria.git
GitKeyRouter.exe test-route camus0109 --url https://github.com/camus0109/panel-terraria.git --connect
GitKeyRouter.exe test-ssh github-camus
GitKeyRouter.exe test-ssh github-camus --verbose
GitKeyRouter.exe version
GitKeyRouter.exe help
```

`apply` 默认只显示 SSH diff 和 Git rewrite 计划。只有带 `--yes` 才执行修改。`apply-profiles` 采用相同策略，默认只显示条件 Git Config diff。

配置写入会校验加载时的文件存在状态和 SHA-256；如果配置被其他程序替换或编辑，本次保存会拒绝覆盖并要求重新加载。Git rewrite 计划也保存所有受影响 key 在预览时的精确有序值，应用时只要这些值发生变化就拒绝旧计划；计划外 key 的变化不受影响。

GUI 与带 `--yes` 的 `apply` / `apply-profiles` 共享排他锁，防止跨进程并发写入。其余 CLI 命令不取得该锁；`version` / `--version` 和 `help` / `--help` 还会在加载配置与创建应用服务之前直接返回，因此 GUI 运行时仍可用于脚本和发布验证。

`test-route --connect` 必须同时提供真实 `--url`，避免程序对虚构仓库发起网络请求。

CLI 诊断退出码：

- `0`：无警告和错误
- `1`：存在警告
- `2`：存在错误或连接测试失败
- `3`：参数错误或程序自身执行失败
- `4`：GUI 或带 `--yes` 的写入命令需要排他锁，但当前 Windows 用户已有 GitKeyRouter GUI 或写入操作运行

## 配置示例

```json
{
  "SchemaVersion": 4,
  "GitServices": [
    {
      "Id": "github.com",
      "DisplayName": "GitHub.com",
      "ProviderKind": "GitHub",
      "HostName": "github.com",
      "SshPort": null,
      "SshUser": "git",
      "WebBaseUrl": "https://github.com",
      "AllowInsecureHttp": false,
      "EnableExtendedSshUrlRewrites": false,
      "IsBuiltIn": true
    }
  ],
  "Identities": [
    {
      "Id": "7b90999f7ce643fbb07eb4b94f802579",
      "ServiceInstanceId": "github.com",
      "DisplayName": "Camus GitHub",
      "AccountName": "camus0109",
      "HostAlias": "github-camus",
      "PrivateKeyPath": "C:\\Users\\fgc01\\.ssh\\id_ed25519_github_camus",
      "PublicKeyPath": "C:\\Users\\fgc01\\.ssh\\id_ed25519_github_camus.pub",
      "EmailOrComment": "camus0109",
      "CreatedAt": "2026-07-18T08:30:45+00:00"
    }
  ],
  "RepositoryRoutes": [
    {
      "ServiceInstanceId": "github.com",
      "NamespacePath": "camus0109",
      "IdentityId": "7b90999f7ce643fbb07eb4b94f802579",
      "Enabled": true
    }
  ]
}
```

## 配置升级

当前使用 Schema 4。`SchemaVersion` 属性名按 JSON 的大小写不敏感规则读取，因此 `schemaVersion` 等写法不会被误判为 Schema 1；应用加载、备份清单和恢复校验使用同一规则。缺失版本属性仍按 Schema 1 迁移，重复、非整数、无效或高于当前版本的值会在不修改原文件的前提下被拒绝。读取旧配置时会保留全部服务、身份、密钥路径、仓库路由和 Git Profiles；对于配置了默认身份但缺少托管服务路由的服务，会在规范化时派生服务级路由。旧 Gitea 账号级 rewrite 不会自动删除，只有用户确认迁移后才转换。GitHub Owner 路由保持兼容。修改配置前仍会创建快照。

## 输入校验

GitHub Owner 和 HostAlias 使用：

```regex
^[A-Za-z0-9_.-]+$
```

额外限制：

- GitHub Owner 不允许斜杠；GitLab、Gitea 和通用服务允许以 `/` 分隔的多级 Namespace
- HostAlias 不允许空格、斜杠、冒号、通配符或控制字符
- HostAlias 不能直接使用已配置服务的真实主机名
- 每个身份的 HostAlias 必须唯一
- 同一 Git 服务中的一个启用 Namespace 只能指向一个身份
- 路由身份必须属于同一个 Git 服务
- 私钥和公钥路径必须为不同的绝对路径

## 本地构建和验证

在安装 .NET 10 SDK 的 Windows PowerShell 中：

```powershell
dotnet restore .\GitKeyRouter.sln --locked-mode
dotnet restore .\src\GitKeyRouter.App\GitKeyRouter.App.csproj `
  -r win-x64 `
  --locked-mode `
  -p:NuGetLockFilePath=packages.publish-win-x64.lock.json `
  -p:PublishSingleFile=true
dotnet format .\GitKeyRouter.sln --verify-no-changes --no-restore
dotnet build .\GitKeyRouter.sln -c Release --no-restore
dotnet test .\GitKeyRouter.sln -c Release --no-build --no-restore
dotnet publish .\src\GitKeyRouter.App\GitKeyRouter.App.csproj `
  -c Release `
  -r win-x64 `
  --no-restore `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:EnableCompressionInSingleFile=true
```

也可以在解决方案根目录双击或从终端运行：

```text
Publish-WinX64.bat
Publish-WinX64-SelfContained.bat
Publish-WinX64-FrameworkDependent.bat
```

三者都调用 `scripts\Publish-WinX64.ps1`，保持同一套格式化、构建、测试、发布和 EXE 校验逻辑。BAT 会先显示仓库根目录和目标目录，成功后自动打开实际输出文件夹；失败时保留窗口显示错误。`Publish-WinX64.bat` 还会生成 `artifacts\release` 下的版本化 ZIP 与 `SHA256SUMS.txt`。

暂时跳过测试：

```powershell
.\scripts\Publish-WinX64.ps1 -SkipTests
```

最终发布目录：

```text
artifacts\publish\win-x64\                         # 自包含版，包含 GitKeyRouter.exe
artifacts\publish\win-x64-framework-dependent\     # 依赖框架版，包含 GitKeyRouter.exe
artifacts\release\                                  # 带版本号的 ZIP 和 SHA256SUMS.txt
```

这些目录属于本地生成物并被 `.gitignore` 忽略，不会随 Git 提交或分支合并复制。若在隔离工作区中执行发布，生成物也只存在于该工作区；需要在当前仓库根目录重新运行 BAT 才会出现在当前仓库的 `artifacts` 下。

## 测试隔离

进程操作通过接口抽象，绝大多数测试使用内存或临时目录。

Git 集成测试通过：

```text
GIT_CONFIG_GLOBAL=<temporary-file>
```

隔离全局配置，不修改开发机真实 Git 配置。

## 常见错误

### 找不到 git.exe

安装 Git for Windows 或将其加入 `PATH`。GitKeyRouter 不会自动安装。

### 找不到 ssh.exe / ssh-keygen.exe

在 Windows“可选功能”中启用 OpenSSH Client，或确认 Git for Windows 的 OpenSSH 目录存在。

### Permission denied (publickey)

检查：

1. 公钥是否添加到正确 Git 服务账户
2. SSH Config 的 IdentityFile 是否指向正确私钥
3. HostAlias 是否与仓库路由 rewrite 的 Base URL 一致
4. 私钥文件是否存在

### Could not resolve hostname github-camus

通常表示 SSH Config 中缺少对应 `Host github-camus`，或 managed block 尚未同步。

### Host key verification failed

检查 `%USERPROFILE%\.ssh\known_hosts` 和当前网络环境。GitKeyRouter 不会自动删除 host key。

### URL 没有被重写

在“Git 重写配置”检查：

- Git 服务是否选择了属于本服务的默认身份，以及对应 `service-default:<serviceId>` 路由是否存在
- 当前实际规则与期望服务级规则是否一致
- HTTPS / SSH insteadOf 是否为 `Correct`，或是否显示缺失/旧路由迁移状态
- 是否存在更长或冲突的前缀
- 输入 URL 是否包含与 Namespace 对应的完整路径前缀

### config.json 损坏

程序会停止保存并显示 JSON 解析错误，不会自动覆盖损坏文件。请手动修复或从“备份与恢复”恢复。

## 项目结构

```text
src/
  GitKeyRouter.App/             WinForms、CLI、界面编排
  GitKeyRouter.Core/            模型、校验、业务服务、诊断
  GitKeyRouter.Infrastructure/  Git、SSH、进程、文件、备份实现

tests/
  GitKeyRouter.Tests/           单元测试和隔离 Git 集成测试
```

## 文档与设计

- [Architecture](docs/architecture.md)
- [Backup and restore](docs/backup-and-restore.md)
- [Troubleshooting](docs/troubleshooting.md)
- [优化需求完成状态与后续规划](docs/project-optimization-status.md)
- [版本记录索引](docs/version-records.md)
- [Security policy](SECURITY.md)
- [Contributing guide](CONTRIBUTING.md)
- [English README](README.en.md)

## 许可证

本项目采用 [MIT License](LICENSE)。
