using System.Text;
using GitKeyRouter.App.Forms;
using GitKeyRouter.App.Presentation;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;

namespace GitKeyRouter.App.Controls;

public sealed class GitServicesControl : UserControl, IAsyncRefreshable
{
    private readonly ApplicationServices _services;
    private readonly Action<string> _status;
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<GitServiceInstance> _items = [];
    private IReadOnlyList<GitIdentity> _identities = [];

    public GitServicesControl(ApplicationServices services, Action<string> status)
    {
        _services = services;
        _status = status;
        var header = UiHelpers.CreatePageHeader(
            AppLocalization.T("Git 服务", "Git Services"),
            AppLocalization.T("管理 GitHub.com、GitLab、Gitea 与其他自建 Git 服务实例", "Manage GitHub.com, GitLab, Gitea, and other self-hosted Git services"),
            AppLocalization.T(
                "Git 服务描述远端主机和连接方式。\r\n\r\n• 先新建服务，再到“Git 身份”创建账号和密钥。\r\n• 默认身份是该服务的兜底身份。\r\n• Owner 和仓库路由会优先于默认身份。\r\n• 选择默认身份后，使用“应用服务配置”同步 SSH 与 Git 重写规则。\r\n• 应用前会显示变更预览。",
                "A Git service describes a remote host and how to connect to it.\r\n\r\n• Create the service first, then create an account and key under Git Identities.\r\n• The default identity is the fallback for this service.\r\n• Owner and repository routes override the default identity.\r\n• After selecting a default identity, use Apply service configuration to synchronize SSH and Git rewrite rules.\r\n• Changes are previewed before they are applied."));
        var toolbar = UiHelpers.CreateToolbar();
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("新建", "New"), async (_, _) => await CreateAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("编辑", "Edit"), async (_, _) => await EditAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("删除", "Delete"), async (_, _) => await DeleteAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("应用服务配置", "Apply service configuration"), async (_, _) => await ApplyServiceConfigurationAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("测试连接", "Test connection"), async (_, _) => await TestConnectionAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("刷新", "Refresh"), async (_, _) => await RefreshAsync()));
        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(header);
        _grid.CellDoubleClick += async (_, _) => await EditAsync();
    }

    public async Task RefreshAsync()
    {
        var config = await _services.ConfigStore.LoadAsync();
        _items = await _services.GitServiceService.ListAsync();
        _identities = config.Identities;
        _grid.DataSource = _items.Select(item => new ServiceRow
        {
            Id = item.Id,
            名称 = item.DisplayName,
            类型 = item.ProviderKind.ToString(),
            主机 = item.HostName,
            SSH端口 = item.SshPort?.ToString() ?? "默认",
            SSH用户 = item.SshUser,
            Web地址 = item.WebBaseUrl,
            默认身份 = _identities.FirstOrDefault(identity => string.Equals(identity.Id, item.DefaultIdentityId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "<无>",
            服务级路由 = config.RepositoryRoutes.Any(route => route.Enabled
                && route.Scope == GitRouteScope.Service
                && string.Equals(route.ServiceInstanceId, item.Id, StringComparison.OrdinalIgnoreCase)) ? "已启用" : "未启用",
            内置 = item.IsBuiltIn ? "是" : "否"
        }).ToList();
        var webAddressColumn = _grid.Columns["Web地址"];
        if (webAddressColumn is not null)
        {
            webAddressColumn.MinimumWidth = 220;
        }
        UiHelpers.ResizeGridColumns(_grid);
        _status($"已加载 {_items.Count} 个 Git 服务");
    }

    private async Task CreateAsync()
    {
        using var form = new GitServiceEditForm(identities: _identities);
        if (form.ShowDialog(this) != DialogResult.OK || form.ResultService is null)
        {
            return;
        }

        await SaveAsync(form.ResultService);
    }

    private async Task EditAsync()
    {
        var selected = SelectedService();
        if (selected is null)
        {
            return;
        }

        using var form = new GitServiceEditForm(selected, _identities);
        if (form.ShowDialog(this) != DialogResult.OK || form.ResultService is null)
        {
            return;
        }

        await SaveAsync(form.ResultService);
    }

    private async Task SaveAsync(GitServiceInstance service)
    {
        var result = await _services.GitServiceService.SaveAsync(service);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedService();
        if (selected is null)
        {
            return;
        }

        if (selected.IsBuiltIn)
        {
            MessageBox.Show(this, "内置 GitHub.com 服务不能删除。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, $"删除 Git 服务“{selected.DisplayName}”？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var result = await _services.GitServiceService.DeleteAsync(selected.Id);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
    }

    private async Task ApplyServiceConfigurationAsync()
    {
        var selected = SelectedService();
        if (selected is null)
        {
            return;
        }

        var config = await _services.ConfigStore.LoadAsync();
        var identity = string.IsNullOrWhiteSpace(selected.DefaultIdentityId)
            ? null
            : config.Identities.FirstOrDefault(item =>
                string.Equals(item.Id, selected.DefaultIdentityId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ServiceInstanceId, selected.Id, StringComparison.OrdinalIgnoreCase));
        if (identity is null)
        {
            MessageBox.Show(
                this,
                AppLocalization.T(
                    "该服务尚未配置属于本服务的默认身份。请先编辑服务并选择默认身份。",
                    "This service does not have a default identity from the same service. Edit the service and select a default identity first."),
                "GitKeyRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var rawSshConfig = await _services.SshConfigService.ReadRawAsync();
        var sshPreview = _services.SshConfigService.PreviewUpsert(rawSshConfig, selected, identity);
        var gitPlan = await _services.GitUrlRewriteService.BuildServiceRepairPlanAsync(selected.Id);
        if (!sshPreview.HasChanges && !gitPlan.HasChanges)
        {
            _status($"{selected.DisplayName} 无需修改");
            MessageBox.Show(this, "无需修改。SSH managed block 与服务级 Git rewrite 均已正确。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var changeText = new StringBuilder()
            .AppendLine($"Git 服务：{selected.DisplayName}")
            .AppendLine($"默认身份：{identity.DisplayName} ({identity.HostAlias})")
            .AppendLine()
            .AppendLine("SSH Config 精确 diff：")
            .AppendLine(sshPreview.DiffText)
            .AppendLine()
            .AppendLine(UiHelpers.FormatGitPlan(gitPlan))
            .ToString();
        using var diff = new DiffPreviewForm("应用服务配置", changeText);
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _status($"正在协调 {selected.DisplayName}...");
        var sshResult = await _services.SshConfigService.ApplyAsync(
            sshPreview,
            $"Apply Git service SSH config: {selected.DisplayName}");
        if (!sshResult.Success)
        {
            UiHelpers.ShowErrors(this, sshResult);
            return;
        }

        var gitResult = await _services.GitUrlRewriteService.ApplyPlanAsync(
            gitPlan,
            $"Apply Git service routes: {selected.DisplayName}");
        if (!gitResult.Success)
        {
            UiHelpers.ShowErrors(this, gitResult);
            return;
        }

        var previewUrl = $"{selected.WebBaseUrl.TrimEnd('/')}/__gitkeyrouter__/__route_preview__.git";
        var routePreview = await _services.GitUrlRewriteService.PreviewAsync(previewUrl);
        var diagnostics = await _services.DiagnosticService.RunAsync();
        await RefreshAsync();
        _status($"{selected.DisplayName} 服务配置已协调");
        MessageBox.Show(
            this,
            $"服务配置已应用。\r\n\r\nSSH managed block：{(sshPreview.HasChanges ? "已同步" : "无需修改")}\r\nGit rewrite：{(gitPlan.HasChanges ? "已协调" : "无需修改")}\r\n预期重写：{routePreview.ExpectedRewrittenUrl ?? routePreview.RewrittenUrl}\r\n诊断：{diagnostics.ErrorCount} 个错误，{diagnostics.WarningCount} 个警告。",
            "GitKeyRouter",
            MessageBoxButtons.OK,
            diagnostics.ErrorCount == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task TestConnectionAsync()
    {
        var selected = SelectedService();
        if (selected is null)
        {
            return;
        }

        _status($"正在测试 {selected.DisplayName} 的 SSH 连接...");
        var result = await _services.GitServiceService.TestConnectionAsync(selected);
        if (!result.Success || result.Value is null)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        if (!result.Value.Authenticated
            && SshHostTrustService.IsHostKeyVerificationFailure(result.Value.Process)
            && await SshHostTrustUi.PromptAndTrustAsync(this, _services, selected))
        {
            _status("主机密钥已信任，正在重新测试 Git 服务...");
            result = await _services.GitServiceService.TestConnectionAsync(selected);
            if (!result.Success || result.Value is null)
            {
                UiHelpers.ShowErrors(this, result);
                return;
            }
        }

        CommandResultForm.ShowProcess(this, $"{selected.DisplayName} - {result.Value.Classification}", result.Value.Process);
        _status(result.Value.Authenticated ? "Git 服务认证成功" : result.Value.Classification);
    }

    private GitServiceInstance? SelectedService()
    {
        if (_grid.CurrentRow?.DataBoundItem is not ServiceRow row)
        {
            MessageBox.Show(this, "请先选择一个 Git 服务。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return _items.FirstOrDefault(item => string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ServiceRow
    {
        public string Id { get; init; } = string.Empty;
        public string 名称 { get; init; } = string.Empty;
        public string 类型 { get; init; } = string.Empty;
        public string 主机 { get; init; } = string.Empty;
        public string SSH端口 { get; init; } = string.Empty;
        public string SSH用户 { get; init; } = string.Empty;
        public string Web地址 { get; init; } = string.Empty;
        public string 默认身份 { get; init; } = string.Empty;
        public string 服务级路由 { get; init; } = string.Empty;
        public string 内置 { get; init; } = string.Empty;
    }
}
