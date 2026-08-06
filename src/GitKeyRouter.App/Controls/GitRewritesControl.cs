using GitKeyRouter.App.Forms;
using GitKeyRouter.App.Presentation;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;

namespace GitKeyRouter.App.Controls;

public sealed class GitRewritesControl : UserControl, IAsyncRefreshable
{
    private readonly ApplicationServices _services;
    private readonly Action<string> _status;
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private readonly TextBox _urlInput = new() { Dock = DockStyle.Fill, PlaceholderText = "https://git.example.com/group/repository.git" };
    private readonly TextBox _previewOutput = UiHelpers.CreateOutputTextBox();
    private IReadOnlyList<GitRewriteComparison> _comparisons = [];
    private IReadOnlyList<GitServiceInstance> _gitServices = [];

    public GitRewritesControl(ApplicationServices services, Action<string> status)
    {
        _services = services;
        _status = status;
        var header = UiHelpers.CreatePageHeader(
            AppLocalization.T("Git 重写配置", "Git URL Rewrites"),
            AppLocalization.T("检查并修复 Git URL 到 SSH HostAlias 的重写规则", "Inspect and repair rules that rewrite Git URLs to SSH HostAliases"),
            AppLocalization.T(
                "Git 重写使用全局 url.*.insteadOf 规则，把 HTTPS 或普通 SSH 地址映射到指定 HostAlias。\r\n\r\n• 先在下方输入仓库 URL 并使用“本地预览”。\r\n• 仓库前缀最长的规则优先，因此仓库 > Owner > 服务默认身份。\r\n• “应用缺失配置”只补充缺失项；“修复当前全部路由”会协调差异。\r\n• “实际连接测试”会调用 Git/SSH 验证最终地址。",
                "Git rewrites use global url.*.insteadOf rules to map HTTPS or normal SSH URLs to a selected HostAlias.\r\n\r\n• Enter a repository URL below and use Local preview first.\r\n• Git chooses the longest matching prefix, so repository > owner > service default identity.\r\n• Apply missing configuration only adds missing entries; Reconcile all current routes aligns differences.\r\n• Actual connection test invokes Git/SSH against the final address."));
        var toolbar = UiHelpers.CreateToolbar();
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("应用缺失配置", "Apply missing configuration"), async (_, _) => await ApplyPlanAsync(await _services.GitUrlRewriteService.BuildApplyMissingPlanAsync(), AppLocalization.T("应用缺失配置", "Apply missing configuration"))));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("修复当前全部路由", "Reconcile all current routes"), async (_, _) => await ApplyPlanAsync(await _services.GitUrlRewriteService.BuildReconcilePlanAsync(), AppLocalization.T("修复 Git rewrite", "Reconcile Git rewrites"))));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("清理重复规则", "Clean duplicate rules"), async (_, _) => await ApplyPlanAsync(await _services.GitUrlRewriteService.BuildCleanupDuplicatesPlanAsync(), AppLocalization.T("清理重复 Git rewrite", "Clean duplicate Git rewrites"))));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("全部重新生成", "Regenerate all"), async (_, _) => await ApplyPlanAsync(await _services.GitUrlRewriteService.BuildRegeneratePlanAsync(), AppLocalization.T("全部重新生成受管理 Git rewrite", "Regenerate all managed Git rewrites"))));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("转换旧版账号路由", "Convert legacy account routes"), async (_, _) => await ApplyPlanAsync(await _services.GitUrlRewriteService.BuildLegacyAccountOwnerMigrationPlanAsync(), AppLocalization.T("转换旧版“账号即 Owner”路由", "Convert legacy account-as-owner routes"))));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("删除选中规则", "Delete selected rule"), async (_, _) => await DeleteSelectedAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("复制对应命令", "Copy matching command"), (_, _) => CopySelectedCommand()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("刷新", "Refresh"), async (_, _) => await RefreshAsync()));

        var testPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(6),
            BackColor = UiHelpers.AppBackground
        };
        testPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        testPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        testPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        testPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        testPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        testPanel.Controls.Add(_urlInput, 0, 0);
        var previewButton = new Button { Text = AppLocalization.T("本地预览", "Local preview"), Dock = DockStyle.Fill };
        previewButton.Click += async (_, _) => await PreviewUrlAsync();
        var connectButton = new Button { Text = AppLocalization.T("实际连接测试", "Actual connection test"), Dock = DockStyle.Fill };
        connectButton.Click += async (_, _) => await TestConnectionAsync();
        testPanel.Controls.Add(previewButton, 1, 0);
        testPanel.Controls.Add(connectButton, 2, 0);
        var previewPanel = UiHelpers.CreateOutputPanel(_previewOutput);
        previewPanel.Margin = new Padding(3, 6, 3, 3);
        testPanel.Controls.Add(previewPanel, 0, 1);
        testPanel.SetColumnSpan(previewPanel, 3);

        Controls.Add(_grid);
        Controls.Add(testPanel);
        Controls.Add(toolbar);
        Controls.Add(header);
        UiHelpers.EnableStatusColors(_grid, nameof(RewriteRow.状态));
    }

    public async Task RefreshAsync()
    {
        try
        {
            var configTask = _services.ConfigStore.LoadAsync();
            var comparisonsTask = _services.GitUrlRewriteService.CompareAsync();
            await Task.WhenAll(configTask, comparisonsTask);
            _gitServices = configTask.Result.GitServices;
            _comparisons = comparisonsTask.Result;
            _grid.DataSource = _comparisons.Select((item, index) => new RewriteRow
            {
                Index = index,
                Git服务 = ServiceName(item.ServiceInstanceId),
                命名空间 = item.NamespacePath ?? "<额外配置>",
                身份 = item.IdentityDisplayName ?? string.Empty,
                BaseURL = item.ExpectedBaseUrl,
                InsteadOfURL = item.InsteadOfUrl,
                状态 = item.Status.ToString(),
                匹配数量 = item.ActualMatchCount
            }).ToList();
            if (_grid.Columns[nameof(RewriteRow.Index)] is { } indexColumn)
            {
                indexColumn.Visible = false;
            }

            _status($"已读取 {_comparisons.Count} 条 Git rewrite 对比结果");
        }
        catch (Exception exception)
        {
            _comparisons = [];
            _grid.DataSource = null;
            _previewOutput.Text = exception.ToString();
            _status("读取 Git rewrite 失败");
        }
    }

    private async Task ApplyPlanAsync(GitRewritePlan plan, string title)
    {
        if (!plan.HasChanges)
        {
            _status("无需修改；Git URL rewrite 已与期望配置一致");
            MessageBox.Show(this, "无需修改。当前 Git URL rewrite 已与期望配置一致。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var diff = new DiffPreviewForm(title, UiHelpers.FormatGitPlan(plan));
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.GitUrlRewriteService.ApplyPlanAsync(plan, title);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        if (result.Value is { Count: > 0 })
        {
            using var command = new CommandResultForm(
                "Git config 执行结果",
                string.Join(Environment.NewLine + Environment.NewLine, result.Value.Select(UiHelpers.FormatProcess)));
            command.ShowDialog(this);
        }

        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(_urlInput.Text))
        {
            await PreviewUrlAsync();
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedComparison();
        if (selected is null)
        {
            return;
        }

        var plan = await _services.GitUrlRewriteService.BuildDeleteRulePlanAsync(selected.ExpectedBaseUrl, selected.InsteadOfUrl);
        await ApplyPlanAsync(plan, "删除指定 Git rewrite");
    }

    private void CopySelectedCommand()
    {
        var selected = SelectedComparison();
        if (selected is null)
        {
            return;
        }

        var command = $"git config --global --add \"url.{selected.ExpectedBaseUrl}.insteadOf\" \"{selected.InsteadOfUrl}\"";
        Clipboard.SetText(command);
        _status("Git 命令已复制；实际执行使用独立参数，不使用此 shell 文本");
    }

    private async Task PreviewUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(_urlInput.Text))
        {
            return;
        }

        var remoteUrl = _urlInput.Text.Trim();
        var parseTask = _services.GitUrlRewriteService.ParseRemoteUrlAsync(remoteUrl);
        var previewTask = _services.GitUrlRewriteService.PreviewAsync(remoteUrl);
        await Task.WhenAll(parseTask, previewTask);
        var parsed = parseTask.Result;
        var preview = previewTask.Result;
        var expectationMessage = preview.ExpectationStatus switch
        {
            UrlRewriteExpectationStatus.Applied => "期望路由已应用，当前 Git 全局配置与服务配置一致。",
            UrlRewriteExpectationStatus.Missing => "期望路由存在，但当前 Git 全局配置缺失。可点击“应用缺失配置”或“修复当前全部路由”。",
            UrlRewriteExpectationStatus.Conflict => "期望路由存在，但当前 Git 全局配置指向其他目标。可点击“修复当前全部路由”。",
            _ => "未能从当前 GitKeyRouter 配置推导出期望路由。"
        };
        _previewOutput.Text = string.Join("\r\n",
        [
            $"原始 URL：{preview.OriginalUrl}",
            $"识别服务：{parsed?.ServiceDisplayName ?? "<未识别>"}",
            $"URL 格式：{parsed?.PatternKind.ToString() ?? "<未识别>"}",
            $"命名空间：{parsed?.NamespacePath ?? "<未识别>"}",
            $"仓库名称：{parsed?.RepositoryName ?? "<未识别>"}",
            $"当前匹配前缀：{preview.ActualMatchedPrefix ?? "<无>"}",
            $"当前目标 Base：{preview.ActualMatchedBaseUrl ?? "<无>"}",
            $"当前重写结果：{preview.ActualRewrittenUrl ?? preview.OriginalUrl}",
            $"匹配前缀：{preview.ExpectedMatchedPrefix ?? "<无>"}",
            $"目标 Base：{preview.ExpectedMatchedBaseUrl ?? "<无>"}",
            $"重写结果：{preview.ExpectedRewrittenUrl ?? preview.RewrittenUrl}",
            $"期望路由状态：{preview.ExpectationStatus}",
            expectationMessage
        ]);
    }

    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_urlInput.Text))
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "实际连接测试将执行 git ls-remote，并可能等待网络和 SSH 认证。是否继续？",
                "实际连接测试",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        if (!await GitSshBackendUi.EnsureOpenSshAsync(this, _services))
        {
            _status("Git 未使用兼容的 OpenSSH 后端，连接测试已停止");
            return;
        }

        _status("正在执行 git ls-remote...");
        var remoteUrl = _urlInput.Text.Trim();
        var result = await _services.GitUrlRewriteService.TestRemoteRouteAsync(remoteUrl);
        var failedService = result.Service;
        if (!result.AuthenticationSucceeded
            && failedService is not null
            && SshHostTrustService.IsHostKeyVerificationFailure(result.Process)
            && await SshHostTrustUi.PromptAndTrustAsync(this, _services, failedService))
        {
            _status("主机密钥已信任，正在重新执行 git ls-remote...");
            result = await _services.GitUrlRewriteService.TestRemoteRouteAsync(remoteUrl);
        }

        CommandResultForm.ShowProcess(this, $"git ls-remote - {result.Classification}", result.Process);
        _status(result.AuthenticationSucceeded ? "SSH 公钥认证已确认" : result.Classification);
    }

    private GitRewriteComparison? SelectedComparison()
    {
        if (_grid.CurrentRow?.DataBoundItem is not RewriteRow row || row.Index < 0 || row.Index >= _comparisons.Count)
        {
            MessageBox.Show(this, "请先选择一条 Git rewrite。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return _comparisons[row.Index];
    }

    private string ServiceName(string? serviceInstanceId)
        => string.IsNullOrWhiteSpace(serviceInstanceId)
            ? "<额外配置>"
            : _gitServices.FirstOrDefault(item =>
                string.Equals(item.Id, serviceInstanceId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? serviceInstanceId;

    private sealed class RewriteRow
    {
        public int Index { get; init; }
        public string Git服务 { get; init; } = string.Empty;
        public string 命名空间 { get; init; } = string.Empty;
        public string 身份 { get; init; } = string.Empty;
        public string BaseURL { get; init; } = string.Empty;
        public string InsteadOfURL { get; init; } = string.Empty;
        public string 状态 { get; init; } = string.Empty;
        public int 匹配数量 { get; init; }
    }
}
