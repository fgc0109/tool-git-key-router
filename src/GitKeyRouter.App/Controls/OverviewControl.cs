using GitKeyRouter.App.Presentation;
using GitKeyRouter.Core.Diagnostics;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.App.Controls;

public sealed class OverviewControl : UserControl, IAsyncRefreshable
{
    private readonly ApplicationServices _services;
    private readonly Action<string> _status;
    private readonly Func<string, Task> _navigate;
    private readonly FlowLayoutPanel _cardsPanel = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = UiHelpers.AppBackground,
        Padding = new Padding(0, 2, 0, 0),
        Margin = Padding.Empty
    };
    private readonly OverviewStatusCard _environmentCard;
    private readonly OverviewStatusCard _identitiesCard;
    private readonly OverviewStatusCard _ownerRoutesCard;
    private readonly OverviewStatusCard _sshConfigCard;
    private readonly OverviewStatusCard _gitRewritesCard;
    private readonly OverviewStatusCard _backupCard;

    public OverviewControl(ApplicationServices services, Action<string> status, Func<string, Task> navigate)
    {
        _services = services;
        _status = status;
        _navigate = navigate;

        var header = UiHelpers.CreatePageHeader(
            AppLocalization.T("概览", "Overview"),
            AppLocalization.T("快速检查工具链、身份、路由和备份状态", "Quickly review toolchain, identities, routes, and backups"),
            AppLocalization.T(
                "建议按以下顺序开始：\r\n\r\n1. 检测 Git、SSH 和 ssh-keygen。\r\n2. 创建 Git 服务和身份。\r\n3. 同步 SSH Config。\r\n4. 创建服务、Owner 或仓库路由并应用 Git 重写。\r\n5. 最后运行一键诊断并创建备份。",
                "Recommended setup order:\r\n\r\n1. Check Git, SSH, and ssh-keygen.\r\n2. Create Git services and identities.\r\n3. Synchronize SSH Config.\r\n4. Create service, owner, or repository routes and apply Git rewrites.\r\n5. Run diagnostics and create a backup."));
        var toolbar = UiHelpers.CreateToolbar();
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("刷新", "Refresh"), async (_, _) => await RefreshAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("一键诊断", "Run diagnostics"), async (_, _) => await RunDiagnosticsAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("检测/安装必需软件", "Check/install required tools"), async (_, _) => await CheckRequiredToolsAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("打开配置目录", "Open config folder"), (_, _) => OpenDirectory(_services.Paths.AppDataDirectory)));

        _environmentCard = CreateOverviewCard(
            "环境状态",
            "正在读取 Git、SSH 与安装工具状态...",
            "读取中",
            OverviewStatusKind.Unknown,
            "查看诊断",
            async (_, _) => await _navigate(PageKeys.Diagnostics));
        _identitiesCard = CreateOverviewCard(
            "Git 身份",
            "正在读取身份与密钥文件状态...",
            "读取中",
            OverviewStatusKind.Unknown,
            "去管理",
            async (_, _) => await _navigate(PageKeys.Identities));
        _ownerRoutesCard = CreateOverviewCard(
            "仓库路由",
            "正在读取服务命名空间与身份映射...",
            "读取中",
            OverviewStatusKind.Unknown,
            "查看路由",
            async (_, _) => await _navigate(PageKeys.RepositoryRoutes));
        _sshConfigCard = CreateOverviewCard(
            "SSH Config",
            "正在检查受管理 Host 区块...",
            "读取中",
            OverviewStatusKind.Unknown,
            "查看配置",
            async (_, _) => await _navigate(PageKeys.SshConfig));
        _gitRewritesCard = CreateOverviewCard(
            "Git 重写配置",
            "正在比对期望规则与全局 Git 配置...",
            "读取中",
            OverviewStatusKind.Unknown,
            "检查规则",
            async (_, _) => await _navigate(PageKeys.GitRewrites));
        _backupCard = CreateOverviewCard(
            "备份状态",
            "正在读取配置快照...",
            "读取中",
            OverviewStatusKind.Unknown,
            "立即备份",
            async (_, _) => await CreateBackupAsync());

        _cardsPanel.Controls.AddRange(
        [
            _environmentCard,
            _identitiesCard,
            _ownerRoutesCard,
            _sshConfigCard,
            _gitRewritesCard,
            _backupCard
        ]);
        _cardsPanel.SizeChanged += (_, _) => ResizeCards();
        _cardsPanel.Layout += (_, _) => ResizeCards();

        Controls.Add(_cardsPanel);
        Controls.Add(toolbar);
        Controls.Add(header);
    }

    public async Task RefreshAsync()
    {
        _status("正在读取环境和配置...");
        var configTask = TryLoadAsync(() => _services.ConfigStore.LoadAsync());
        var results = await Task.WhenAll(
        [
            RefreshCardAsync(_environmentCard, LoadEnvironmentStateAsync),
            RefreshCardAsync(_identitiesCard, () => LoadIdentityStateAsync(configTask)),
            RefreshCardAsync(_ownerRoutesCard, () => LoadOwnerRouteStateAsync(configTask)),
            RefreshCardAsync(_sshConfigCard, () => LoadSshConfigStateAsync(configTask)),
            RefreshCardAsync(_gitRewritesCard, LoadGitRewriteStateAsync),
            RefreshBackupCardAsync()
        ]);

        var errorCount = results.Count(item => item == OverviewStatusKind.Error);
        var warningCount = results.Count(item => item == OverviewStatusKind.Warning);
        _status(errorCount > 0
            ? $"概览已刷新：{errorCount} 个模块需要修复，{warningCount} 个模块需要注意"
            : warningCount > 0
                ? $"概览已刷新：{warningCount} 个模块需要注意"
                : "概览已刷新：所有模块状态正常");
    }

    private OverviewStatusCard CreateOverviewCard(
        string title,
        string description,
        string statusText,
        OverviewStatusKind statusKind,
        string actionText,
        EventHandler onClick)
    {
        var card = new OverviewStatusCard(title, actionText, onClick);
        card.SetState(description, statusText, statusKind, actionEnabled: false);
        return card;
    }

    private async Task<OverviewStatusKind> RefreshCardAsync(
        OverviewStatusCard card,
        Func<Task<OverviewCardState>> loader)
    {
        card.SetState("正在读取实际状态...", "读取中", OverviewStatusKind.Unknown, actionEnabled: false);
        try
        {
            var state = await loader();
            card.SetState(state.Description, state.StatusText, state.StatusKind, state.ActionEnabled);
            return state.StatusKind;
        }
        catch (Exception exception)
        {
            card.SetState(
                $"读取失败：{GetShortError(exception)}",
                "异常",
                OverviewStatusKind.Error,
                actionEnabled: true);
            return OverviewStatusKind.Error;
        }
    }

    private async Task<OverviewCardState> LoadEnvironmentStateAsync()
    {
        var tools = await _services.ToolchainService.InspectAsync();
        var requiredTools = new[] { tools.Git, tools.Ssh, tools.SshKeygen };
        var missing = requiredTools.Where(item => !item.Exists).Select(item => item.Name).ToList();
        if (missing.Count > 0)
        {
            return new OverviewCardState(
                $"缺少 {string.Join("、", missing)}；请进入诊断查看路径与安装建议",
                $"缺失 {missing.Count} 项",
                OverviewStatusKind.Error);
        }

        if (!tools.Winget.Exists)
        {
            return new OverviewCardState(
                "Git、SSH 和 ssh-keygen 均可用；未检测到 winget，自动安装能力可能受限",
                "需要注意",
                OverviewStatusKind.Warning);
        }


        var backend = await _services.GitSshBackendService.InspectAsync();
        if (!backend.Success || backend.Value is null)
        {
            return new OverviewCardState(
                "Git SSH 后端无法识别；请进入诊断检查 Git SSH 环境覆盖和全局配置",
                "需要注意",
                OverviewStatusKind.Warning);
        }

        if (!backend.Value.IsOpenSsh)
        {
            return new OverviewCardState(
                $"Git 当前使用 {backend.Value.DisplayName}（{backend.Value.Source}）；它不会读取 GitKeyRouter 的 OpenSSH HostAlias、IdentityFile 和 known_hosts",
                "SSH 后端不兼容",
                OverviewStatusKind.Error);
        }

        return new OverviewCardState(
            "Git、OpenSSH、ssh-keygen 和 winget 均已检测到，Git SSH 后端兼容",
            "正常",
            OverviewStatusKind.Normal);
    }

    private async Task<OverviewCardState> LoadIdentityStateAsync(Task<LoadResult<AppConfig>> configTask)
    {
        var config = RequireConfig(await configTask);
        if (config.Identities.Count == 0)
        {
            return new OverviewCardState(
                "尚未配置 Git 身份；创建身份后可管理 SSH 密钥与 HostAlias",
                "未配置",
                OverviewStatusKind.Warning);
        }

        var missingPrivate = config.Identities.Count(identity =>
            string.IsNullOrWhiteSpace(identity.PrivateKeyPath) || !_services.FileSystem.FileExists(identity.PrivateKeyPath));
        var missingPublic = config.Identities.Count(identity =>
            string.IsNullOrWhiteSpace(identity.PublicKeyPath) || !_services.FileSystem.FileExists(identity.PublicKeyPath));
        var missingServices = config.Identities.Count(identity => config.FindService(identity.ServiceInstanceId) is null);
        if (missingServices > 0)
        {
            return new OverviewCardState(
                $"已配置 {config.Identities.Count} 个身份；{missingServices} 个身份引用了不存在的 Git 服务",
                "服务缺失",
                OverviewStatusKind.Error);
        }

        if (missingPrivate > 0 || missingPublic > 0)
        {
            return new OverviewCardState(
                $"已配置 {config.Identities.Count} 个身份；{missingPrivate} 个缺少私钥，{missingPublic} 个缺少公钥",
                "密钥缺失",
                OverviewStatusKind.Error);
        }

        return new OverviewCardState(
            $"{config.GitServices.Count} 个 Git 服务下配置了 {config.Identities.Count} 个身份，所有密钥文件均可用",
            $"{config.Identities.Count} 个身份",
            OverviewStatusKind.Info);
    }

    private static async Task<OverviewCardState> LoadOwnerRouteStateAsync(Task<LoadResult<AppConfig>> configTask)
    {
        var config = RequireConfig(await configTask);
        if (config.RepositoryRoutes.Count == 0)
        {
            return new OverviewCardState(
                "尚未配置仓库路由；可将不同服务的 Owner / Namespace 映射到指定身份",
                "未配置",
                OverviewStatusKind.Warning);
        }

        var identities = config.Identities.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var invalidReferences = config.RepositoryRoutes.Count(route =>
            config.FindService(route.ServiceInstanceId) is null
            || !identities.TryGetValue(route.IdentityId, out var identity)
            || !string.Equals(identity.ServiceInstanceId, route.ServiceInstanceId, StringComparison.OrdinalIgnoreCase));
        var enabledCount = config.RepositoryRoutes.Count(route => route.Enabled);
        var disabledCount = config.RepositoryRoutes.Count - enabledCount;
        if (invalidReferences > 0)
        {
            return new OverviewCardState(
                $"共 {config.RepositoryRoutes.Count} 条路由；{invalidReferences} 条存在服务缺失或身份跨服务引用",
                "需要修复",
                OverviewStatusKind.Error);
        }

        if (disabledCount > 0)
        {
            return new OverviewCardState(
                $"共 {config.RepositoryRoutes.Count} 条路由，其中 {enabledCount} 条启用、{disabledCount} 条停用",
                "部分启用",
                OverviewStatusKind.Warning);
        }

        return new OverviewCardState(
            $"{enabledCount} 条仓库路由均已启用，并且都能找到对应身份",
            $"{enabledCount} 条路由",
            OverviewStatusKind.Info);
    }

    private async Task<OverviewCardState> LoadSshConfigStateAsync(Task<LoadResult<AppConfig>> configTask)
    {
        var config = RequireConfig(await configTask);
        var raw = await _services.SshConfigService.ReadRawAsync();
        var blocks = _services.SshConfigService.ParseManagedBlocks(raw);
        var blockCounts = blocks
            .GroupBy(item => item.HostAlias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var expectedAliases = config.Identities
            .Select(item => item.HostAlias)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var duplicateCount = blockCounts.Values.Count(count => count > 1);
        var missingCount = expectedAliases.Count(alias => !blockCounts.ContainsKey(alias));
        var extraCount = blockCounts.Keys.Count(alias => !expectedAliases.Contains(alias));
        if (expectedAliases.Count == 0 && blockCounts.Count == 0)
        {
            return new OverviewCardState(
                "当前没有身份，也没有 GitKeyRouter 受管理的 SSH Host 区块",
                "无数据",
                OverviewStatusKind.Unknown);
        }

        if (duplicateCount > 0)
        {
            return new OverviewCardState(
                $"检测到 {duplicateCount} 个重复 HostAlias；另有 {missingCount} 个身份区块缺失",
                "配置错误",
                OverviewStatusKind.Error);
        }

        if (missingCount > 0 || extraCount > 0)
        {
            return new OverviewCardState(
                $"受管理区块 {blocks.Count} 个；缺少 {missingCount} 个身份区块，额外 {extraCount} 个区块",
                "部分配置",
                OverviewStatusKind.Warning);
        }

        return new OverviewCardState(
            $"{expectedAliases.Count} 个身份均有唯一的受管理 Host 区块，SSH Config 已同步",
            "已同步",
            OverviewStatusKind.Normal);
    }

    private async Task<OverviewCardState> LoadGitRewriteStateAsync()
    {
        var comparisons = await _services.GitUrlRewriteService.CompareAsync();
        if (comparisons.Count == 0)
        {
            return new OverviewCardState(
                "当前没有需要管理的 Git rewrite 规则；启用仓库路由后会自动生成期望规则",
                "无规则",
                OverviewStatusKind.Unknown);
        }

        var correctCount = comparisons.Count(item => item.Status == GitRewriteStatus.Correct);
        var errorCount = comparisons.Count(item => item.Status is
            GitRewriteStatus.Missing or GitRewriteStatus.Duplicate or GitRewriteStatus.Conflict);
        var warningCount = comparisons.Count(item => item.Status is GitRewriteStatus.Extra or GitRewriteStatus.Disabled);
        if (errorCount > 0)
        {
            return new OverviewCardState(
                $"{correctCount} 条规则正常，{errorCount} 条缺失、重复或冲突，{warningCount} 条需要注意",
                "需要修复",
                OverviewStatusKind.Error);
        }

        if (warningCount > 0)
        {
            return new OverviewCardState(
                $"{correctCount} 条规则正常，另有 {warningCount} 条额外或停用规则需要确认",
                "需要注意",
                OverviewStatusKind.Warning);
        }

        return new OverviewCardState(
            $"{correctCount} 条 Git rewrite 规则均与当前仓库路由一致",
            "已同步",
            OverviewStatusKind.Normal);
    }

    private Task<OverviewStatusKind> RefreshBackupCardAsync()
        => RefreshCardAsync(_backupCard, LoadBackupStateAsync);

    private async Task<OverviewCardState> LoadBackupStateAsync()
    {
        var backups = await _services.BackupService.ListAsync();
        var latest = backups.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        if (latest is null)
        {
            return new OverviewCardState(
                "尚未创建配置快照；建议在修改身份、路由或 SSH 配置前先备份",
                "暂无备份",
                OverviewStatusKind.Warning);
        }

        var latestText = latest.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (!string.IsNullOrWhiteSpace(latest.GitRewriteCaptureError))
        {
            return new OverviewCardState(
                $"最近备份：{latestText}；应用和 SSH 配置已保存，但 Git rewrite 捕获失败",
                "部分成功",
                OverviewStatusKind.Warning);
        }

        return new OverviewCardState(
            $"共有 {backups.Count} 个配置快照，最近一次创建于 {latestText}",
            "已备份",
            OverviewStatusKind.Normal);
    }

    private async Task CreateBackupAsync()
    {
        _backupCard.SetState("正在创建应用、SSH 与 Git rewrite 配置快照...", "处理中", OverviewStatusKind.Info, actionEnabled: false);
        _status("正在创建配置快照...");
        try
        {
            var manifest = await _services.BackupService.CreateSnapshotAsync("Manual backup from overview");
            _status($"已创建备份：{manifest.BackupDirectory}");
            await RefreshBackupCardAsync();
        }
        catch (Exception exception)
        {
            var error = GetShortError(exception);
            _backupCard.SetState($"创建备份失败：{error}", "备份失败", OverviewStatusKind.Error, actionEnabled: true);
            _status("创建备份失败");
            MessageBox.Show(this, error, "创建备份失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResizeCards()
    {
        if (_cardsPanel.ClientSize.Width <= 0)
        {
            return;
        }

        var contentHeight = _cardsPanel.Padding.Vertical
            + _cardsPanel.Controls.Cast<Control>().Sum(control => control.Height + control.Margin.Vertical);
        var reservedScrollbarWidth = contentHeight > _cardsPanel.ClientSize.Height
            ? SystemInformation.VerticalScrollBarWidth
            : 0;
        reservedScrollbarWidth += 2;
        var width = Math.Max(1, _cardsPanel.ClientSize.Width - _cardsPanel.Padding.Horizontal - reservedScrollbarWidth);
        foreach (var card in _cardsPanel.Controls.OfType<OverviewStatusCard>())
        {
            card.Width = width;
        }

        _cardsPanel.HorizontalScroll.Enabled = false;
        _cardsPanel.HorizontalScroll.Visible = false;
    }

    private static async Task<LoadResult<T>> TryLoadAsync<T>(Func<Task<T>> loader)
        where T : class
    {
        try
        {
            return new LoadResult<T>(await loader(), null);
        }
        catch (Exception exception)
        {
            return new LoadResult<T>(null, exception);
        }
    }

    private static AppConfig RequireConfig(LoadResult<AppConfig> result)
    {
        if (result.Error is not null)
        {
            throw new InvalidOperationException("无法读取应用配置。", result.Error);
        }

        return result.Value ?? throw new InvalidOperationException("应用配置读取结果为空。");
    }

    private static string GetShortError(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        var firstLine = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        var text = string.IsNullOrWhiteSpace(firstLine) ? exception.GetType().Name : firstLine;
        return text.Length <= 120 ? text : text[..117] + "...";
    }

    private async Task RunDiagnosticsAsync()
    {
        _status("正在执行一键诊断...");
        var report = await _services.DiagnosticService.RunAsync();
        using var form = new GitKeyRouter.App.Forms.TextViewForm("诊断报告", DiagnosticReportFormatter.Format(report));
        form.ShowDialog(this);
        _status($"诊断完成：错误 {report.ErrorCount}，警告 {report.WarningCount}");
    }

    private async Task CheckRequiredToolsAsync()
    {
        await RequiredToolInstallationUi.CheckAndOfferAsync(this, _services, _status, showHealthyMessage: true);
        await RefreshAsync();
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(path);
        System.Diagnostics.Process.Start(startInfo);
    }

    private sealed record OverviewCardState(
        string Description,
        string StatusText,
        OverviewStatusKind StatusKind,
        bool ActionEnabled = true);

    private sealed record LoadResult<T>(T? Value, Exception? Error)
        where T : class;
}
