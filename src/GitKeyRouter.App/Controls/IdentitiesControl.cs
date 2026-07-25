using GitKeyRouter.App.Forms;
using GitKeyRouter.App.Presentation;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;

namespace GitKeyRouter.App.Controls;

public sealed class IdentitiesControl : UserControl, IAsyncRefreshable
{
    private readonly ApplicationServices _services;
    private readonly Action<string> _status;
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<GitIdentity> _identities = [];
    private IReadOnlyList<GitServiceInstance> _gitServices = [];

    public IdentitiesControl(ApplicationServices services, Action<string> status)
    {
        _services = services;
        _status = status;

        var header = UiHelpers.CreatePageHeader(
            AppLocalization.T("Git 身份", "Git Identities"),
            AppLocalization.T("管理不同 Git 服务的账号、SSH 密钥、公钥格式与连接测试", "Manage accounts, SSH keys, public-key formats, and connection tests"),
            AppLocalization.T(
                "一个 Git 身份由服务、账号、HostAlias 和 SSH 密钥组成。\r\n\r\n1. 新建身份或选择已有私钥。\r\n2. 将“查看公钥”中的内容添加到对应 Git 服务账号。\r\n3. 使用“同步 SSH Config”生成受管理 Host 区块。\r\n4. 使用“测试 SSH”确认密钥生效。\r\n\r\n程序不会上传或显示私钥内容。",
                "A Git identity combines a service, account, HostAlias, and SSH key.\r\n\r\n1. Create an identity or select an existing private key.\r\n2. Add the value shown by View public key to the matching Git account.\r\n3. Use Synchronize SSH Config to create the managed Host block.\r\n4. Use Test SSH to verify the key.\r\n\r\nThe application never uploads or displays private-key contents."));
        var toolbar = UiHelpers.CreateToolbar();
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("新建", "New"), async (_, _) => await CreateAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("编辑", "Edit"), async (_, _) => await EditAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("删除记录", "Delete record"), async (_, _) => await DeleteAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("生成密钥", "Generate key"), async (_, _) => await GenerateKeyAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("重命名密钥", "Rename key"), async (_, _) => await RenameKeyAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("查看公钥", "View public key"), async (_, _) => await ViewPublicKeyAsync(false)));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("复制公钥", "Copy public key"), async (_, _) => await ViewPublicKeyAsync(true)));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("复制指纹", "Copy fingerprint"), (_, _) => CopyFingerprint()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("转换格式", "Convert format"), async (_, _) => await ConvertPublicKeyAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("导出公钥", "Export public key"), async (_, _) => await ExportPublicKeyAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("同步 SSH Config", "Synchronize SSH Config"), async (_, _) => await SyncSshAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("测试 SSH", "Test SSH"), async (_, _) => await TestSshAsync(false)));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("详细测试", "Detailed test"), async (_, _) => await TestSshAsync(true)));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("打开密钥目录", "Open key folder"), (_, _) => OpenKeyDirectory()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("刷新", "Refresh"), async (_, _) => await RefreshAsync()));

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(header);
        _grid.CellDoubleClick += async (_, _) => await EditAsync();
        UiHelpers.EnableStatusColors(
            _grid,
            nameof(IdentityRow.私钥),
            nameof(IdentityRow.OpenSSH格式),
            nameof(IdentityRow.配置路径),
            nameof(IdentityRow.SSH配置),
            nameof(IdentityRow.密钥使用));
    }

    public async Task RefreshAsync()
    {
        var config = await _services.ConfigStore.LoadAsync();
        _identities = config.Identities
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _gitServices = config.GitServices
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var raw = await _services.SshConfigService.ReadRawAsync();
        var blocks = _services.SshConfigService.ParseManagedBlocks(raw);
        var keyUsageCounts = BuildKeyUsageCounts(_identities);
        var rows = new List<IdentityRow>();
        foreach (var identity in _identities)
        {
            var sshConfigStatus = blocks.Count(block => string.Equals(block.HostAlias, identity.HostAlias, StringComparison.OrdinalIgnoreCase)) == 1
                ? "正常"
                : "未同步";
            var variantsResult = await _services.SshKeyService.ListPublicKeyVariantsAsync(identity);
            var variants = variantsResult.Success && variantsResult.Value is not null
                ? variantsResult.Value
                : [];
            if (variants.Count == 0)
            {
                rows.Add(CreateRow(
                    identity,
                    ServiceName(identity.ServiceInstanceId),
                    null,
                    sshConfigStatus,
                    GetKeyUsage(identity, keyUsageCounts)));
                continue;
            }

            rows.AddRange(variants.Select(variant =>
                CreateRow(
                    identity,
                    ServiceName(identity.ServiceInstanceId),
                    variant,
                    sshConfigStatus,
                    GetKeyUsage(identity, keyUsageCounts))));
        }

        _grid.DataSource = rows;
        foreach (var hiddenName in new[] { nameof(IdentityRow.Id), nameof(IdentityRow.PublicKeyPath), nameof(IdentityRow.Fingerprint) })
        {
            if (_grid.Columns[hiddenName] is { } column)
            {
                column.Visible = false;
            }
        }

        if (_grid.Columns[nameof(IdentityRow.SHA256指纹)] is { } fingerprintColumn)
        {
            fingerprintColumn.HeaderText = "SHA256 指纹";
        }

        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.DataBoundItem is IdentityRow row
                && _grid.Columns[nameof(IdentityRow.SHA256指纹)] is { } column)
            {
                gridRow.Cells[column.Index].ToolTipText = row.Fingerprint;
            }
        }

        _status($"已加载 {_identities.Count} 个身份、{rows.Count(row => !string.IsNullOrWhiteSpace(row.PublicKeyPath))} 个公钥变体");
    }

    private async Task CreateAsync()
    {
        var discoveredKeys = await DiscoverPrivateKeysAsync();
        using var form = new IdentityEditForm(
            _services.Paths.SshDirectory,
            _gitServices,
            discoveredKeys: discoveredKeys);
        if (form.ShowDialog(this) != DialogResult.OK || form.ResultIdentity is null)
        {
            return;
        }

        if (!ConfirmSharedKeyUsage(form.ResultIdentity))
        {
            return;
        }

        var result = await _services.IdentityService.SaveAsync(form.ResultIdentity);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
        await OfferSshSyncAsync(form.ResultIdentity, null);
    }

    private async Task EditAsync()
    {
        var identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        var oldAlias = identity.HostAlias;
        var discoveredKeys = await DiscoverPrivateKeysAsync();
        using var form = new IdentityEditForm(
            _services.Paths.SshDirectory,
            _gitServices,
            identity,
            discoveredKeys);
        if (form.ShowDialog(this) != DialogResult.OK || form.ResultIdentity is null)
        {
            return;
        }

        if (!ConfirmSharedKeyUsage(form.ResultIdentity))
        {
            return;
        }

        var result = await _services.IdentityService.SaveAsync(form.ResultIdentity);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
        await OfferSshSyncAsync(form.ResultIdentity, oldAlias);
    }

    private async Task<IReadOnlyList<SshPrivateKeyCandidate>> DiscoverPrivateKeysAsync()
    {
        _status("正在扫描用户 SSH 目录中的密钥...");
        var result = await _services.SshKeyService.DiscoverPrivateKeysAsync(_services.Paths.SshDirectory);
        if (!result.Success || result.Value is null)
        {
            _status("SSH 密钥自动发现失败，仍可手动输入路径");
            return [];
        }

        _status($"已发现 {result.Value.Count} 个可能的私钥");
        return result.Value;
    }

    private async Task DeleteAsync()
    {
        var identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"删除身份记录“{identity.DisplayName}”？\r\n\r\n私钥和公钥文件不会被删除，关联仓库路由会被禁用。",
            "确认删除身份",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        var result = await _services.IdentityService.DeleteAsync(identity.Id);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        var raw = await _services.SshConfigService.ReadRawAsync();
        var preview = _services.SshConfigService.PreviewDelete(raw, identity.HostAlias);
        if (preview.HasChanges
            && MessageBox.Show(this, "同时删除该身份对应的 SSH managed block？", "SSH Config", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            using var diff = new DiffPreviewForm("删除 SSH managed block", preview.DiffText);
            if (diff.ShowDialog(this) == DialogResult.OK)
            {
                await _services.SshConfigService.ApplyAsync(preview, $"Delete SSH block after identity deletion: {identity.HostAlias}");
            }
        }

        await RefreshAsync();
    }

    private async Task GenerateKeyAsync()
    {
        var identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        var exists = _services.FileSystem.FileExists(identity.PrivateKeyPath) || _services.FileSystem.FileExists(identity.PublicKeyPath);
        var overwrite = false;
        if (exists)
        {
            var answer = MessageBox.Show(
                this,
                "目标密钥文件已经存在。\r\n\r\n是：明确覆盖并先备份旧文件\r\n否：返回编辑身份，选择其他文件名\r\n取消：不执行",
                "密钥文件已存在",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel)
            {
                return;
            }

            if (answer == DialogResult.No)
            {
                await EditAsync();
                return;
            }

            overwrite = true;
        }

        var warning = MessageBox.Show(
            this,
            "将调用系统 ssh-keygen 生成 ed25519 密钥。\r\n\r\n第一版默认不设置 passphrase。是否继续？",
            "生成 SSH 密钥",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (warning != DialogResult.Yes)
        {
            return;
        }

        _status("正在调用 ssh-keygen...");
        var result = await _services.SshKeyService.GenerateAsync(identity, overwrite);
        if (!result.Success || result.Value is null)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        CommandResultForm.ShowProcess(this, "ssh-keygen 执行结果", result.Value.Process);
        using var publicKey = new TextViewForm("生成后的公钥", result.Value.PublicKeyText);
        publicKey.ShowDialog(this);
        await RefreshAsync();
        _status("SSH 密钥生成完成");
    }

    private async Task RenameKeyAsync()
    {
        var identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        var currentName = Path.GetFileName(identity.PrivateKeyPath);
        var suggestedName = Path.GetFileName(
            SshKeyService.CreateDefaultPrivateKeyPath(_services.Paths.SshDirectory, identity.HostAlias));
        if (string.Equals(currentName, suggestedName, StringComparison.OrdinalIgnoreCase))
        {
            suggestedName += "_new";
        }

        using var renameForm = new KeyRenameForm(currentName, suggestedName);
        if (renameForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _status("正在生成密钥重命名预览...");
        var planResult = await _services.SshKeyRenameService.BuildPlanAsync(identity.Id, renameForm.NewBaseName);
        if (!planResult.Success || planResult.Value is null)
        {
            UiHelpers.ShowErrors(this, planResult);
            return;
        }

        using var preview = new DiffPreviewForm(
            "密钥文件与配置变更预览",
            UiHelpers.FormatKeyRenamePlan(planResult.Value),
            "确认重命名");
        if (preview.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _status("正在备份并重命名密钥文件...");
        var result = await _services.SshKeyRenameService.ApplyAsync(planResult.Value);
        if (!result.Success || result.Value is null)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
        MessageBox.Show(
            this,
            $"密钥文件、账户路径和 SSH Config 已更新。\r\n\r\n已创建 {result.Value.BackupFiles.Count} 个文件备份。",
            "重命名完成",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        _status("密钥文件及相关配置已重命名");
    }

    private async Task ViewPublicKeyAsync(bool copyOnly)
    {
        var identity = SelectedIdentity();
        var row = SelectedRow();
        if (identity is null || row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.PublicKeyPath))
        {
            MessageBox.Show(this, "该身份还没有公钥变体。可以先生成密钥，或从现有私钥转换出 OpenSSH 公钥。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var result = await _services.SshKeyService.ReadPublicKeyAsync(row.PublicKeyPath, requireOpenSsh: copyOnly);
        if (!result.Success || result.Value is null)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        if (copyOnly)
        {
            Clipboard.SetText(result.Value);
            _status("OpenSSH 公钥已复制，可粘贴到对应 Git 服务的 SSH Key 页面");
            return;
        }

        using var form = new TextViewForm($"{row.公钥格式} - {identity.DisplayName}", result.Value);
        form.ShowDialog(this);
    }

    private async Task ExportPublicKeyAsync()
    {
        var row = SelectedRow();
        if (row is null || string.IsNullOrWhiteSpace(row.PublicKeyPath))
        {
            MessageBox.Show(this, "请先选择一个已存在的公钥变体。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(row.PublicKeyPath),
            Filter = "Public key (*.pub)|*.pub|All files (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.SshKeyService.ExportPublicKeyAsync(row.PublicKeyPath, dialog.FileName, true);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        _status($"公钥已导出：{dialog.FileName}");
    }

    private async Task ConvertPublicKeyAsync()
    {
        var identity = SelectedIdentity();
        var row = SelectedRow();
        if (identity is null || row is null)
        {
            return;
        }

        var sourcePath = row.PublicKeyPath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            if (!_services.FileSystem.FileExists(identity.PrivateKeyPath))
            {
                MessageBox.Show(this, "没有可转换的公钥或私钥文件。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            sourcePath = identity.PrivateKeyPath;
        }

        var inspection = await _services.SshKeyService.InspectKeyFileAsync(sourcePath);
        if (!inspection.Success || inspection.Value is null)
        {
            UiHelpers.ShowErrors(this, inspection);
            return;
        }

        using var form = new KeyFormatConversionForm(inspection.Value.DisplayName, sourcePath);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _status($"正在转换公钥：{inspection.Value.DisplayName} → {form.SelectedFormat}");
        var result = await _services.SshKeyService.ConvertPublicKeyAsync(
            identity,
            sourcePath,
            form.SelectedFormat,
            form.OverwriteExisting);
        if (!result.Success || result.Value is null)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        var details = result.Value.Changed
            ? $"已创建：{result.Value.DestinationPath}"
            : $"文件已存在且内容相同：{result.Value.DestinationPath}";
        if (!string.IsNullOrWhiteSpace(result.Value.BackupFile))
        {
            details += $"\r\n备份：{result.Value.BackupFile}";
        }

        MessageBox.Show(this, details, "公钥转换完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshAsync();
        _status(result.Message);
    }

    private async Task SyncSshAsync()
    {
        var identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        await OfferSshSyncAsync(identity, null, forcePrompt: true);
    }

    private async Task OfferSshSyncAsync(GitIdentity identity, string? previousAlias, bool forcePrompt = false)
    {
        if (!forcePrompt
            && MessageBox.Show(this, "身份已保存。是否立即同步对应的 SSH Config managed block？", "同步 SSH Config", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var raw = await _services.SshConfigService.ReadRawAsync();
        var updated = raw;
        if (!string.IsNullOrWhiteSpace(previousAlias)
            && !string.Equals(previousAlias, identity.HostAlias, StringComparison.OrdinalIgnoreCase))
        {
            updated = _services.SshConfigService.PreviewDelete(updated, previousAlias).UpdatedText;
        }

        var service = _gitServices.FirstOrDefault(item =>
            string.Equals(item.Id, identity.ServiceInstanceId, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            MessageBox.Show(this, "该身份关联的 Git 服务不存在。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        updated = _services.SshConfigService.PreviewUpsert(updated, service, identity).UpdatedText;
        var preview = _services.SshConfigService.CreatePreview(
            $"Synchronize SSH identity: {identity.HostAlias}",
            raw,
            updated);
        using var diff = new DiffPreviewForm("SSH Config 变更预览", preview.DiffText);
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.SshConfigService.ApplyAsync(preview, $"Synchronize identity SSH block: {identity.HostAlias}");
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
        _status("SSH Config 已同步");
    }

    private async Task TestSshAsync(bool verbose)
    {
        var identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        var service = _gitServices.FirstOrDefault(item =>
            string.Equals(item.Id, identity.ServiceInstanceId, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            MessageBox.Show(this, "该身份关联的 Git 服务不存在。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _status($"正在测试 SSH：{identity.HostAlias}");
        var result = await _services.SshKeyService.TestAsync(service, identity, verbose);
        var text = $"Classification: {result.Classification}\r\nAuthenticated: {result.Authenticated}\r\n\r\n{UiHelpers.FormatProcess(result.Process)}";
        using var form = new CommandResultForm("SSH 测试结果", text);
        form.ShowDialog(this);
        _status(result.Authenticated ? "SSH 认证成功" : "SSH 测试未通过");
    }

    private void OpenKeyDirectory()
    {
        var identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(identity.PrivateKeyPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var startInfo = new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        startInfo.ArgumentList.Add(directory);
        System.Diagnostics.Process.Start(startInfo);
    }

    private static IdentityRow CreateRow(
        GitIdentity identity,
        string serviceName,
        SshPublicKeyVariant? variant,
        string sshConfigStatus,
        string keyUsage)
        => new()
        {
            Id = identity.Id,
            PublicKeyPath = variant?.Path ?? string.Empty,
            显示名称 = identity.DisplayName,
            Git服务 = serviceName,
            账号 = identity.AccountName,
            HostAlias = identity.HostAlias,
            私钥 = File.Exists(identity.PrivateKeyPath) ? "存在" : "缺失",
            公钥格式 = variant?.Inspection.DisplayName ?? "缺失",
            公钥文件 = variant?.FileName ?? Path.GetFileName(identity.PublicKeyPath),
            Fingerprint = variant?.Inspection.Fingerprint ?? string.Empty,
            SHA256指纹 = ShortFingerprint(variant?.Inspection.Fingerprint),
            OpenSSH格式 = variant?.Inspection.IsOpenSsh == true ? "是" : "否",
            配置路径 = variant?.IsConfiguredPath == true ? "是" : "否",
            SSH配置 = sshConfigStatus,
            密钥使用 = keyUsage
        };

    private string ServiceName(string serviceInstanceId)
        => _gitServices.FirstOrDefault(item =>
            string.Equals(item.Id, serviceInstanceId, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? $"缺失：{serviceInstanceId}";

    private void CopyFingerprint()
    {
        var row = SelectedRow();
        if (row is null)
        {
            MessageBox.Show(this, "请先选择一个公钥。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Fingerprint))
        {
            MessageBox.Show(
                this,
                "当前公钥格式无法直接计算标准 OpenSSH SHA256 指纹。请先转换为 OpenSSH 公钥格式。",
                "指纹不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Clipboard.SetText(row.Fingerprint);
        _status($"已复制指纹：{row.Fingerprint}");
    }

    private static string ShortFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return "不可用";
        }

        const int visibleCharacters = 24;
        return fingerprint.Length <= visibleCharacters
            ? fingerprint
            : $"{fingerprint[..visibleCharacters]}…";
    }

    private bool ConfirmSharedKeyUsage(GitIdentity candidate)
    {
        var sharing = _identities
            .Where(item => !string.Equals(item.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)
                && (PathsEqual(item.PrivateKeyPath, candidate.PrivateKeyPath)
                    || PathsEqual(item.PublicKeyPath, candidate.PublicKeyPath)))
            .Select(item => item.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (sharing.Count == 0)
        {
            return true;
        }

        return MessageBox.Show(
            this,
            $"该身份将与以下账户使用相同的私钥或公钥路径：\r\n\r\n- {string.Join("\r\n- ", sharing)}\r\n\r\n这意味着这些账户可能实际使用同一把密钥。仍然保存？",
            "检测到共享密钥",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private static IReadOnlyDictionary<string, int> BuildKeyUsageCounts(IEnumerable<GitIdentity> identities)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in identities)
        {
            foreach (var path in new[] { identity.PrivateKeyPath, identity.PublicKeyPath }
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(NormalizePath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[path] = counts.GetValueOrDefault(path) + 1;
            }
        }

        return counts;
    }

    private static string GetKeyUsage(GitIdentity identity, IReadOnlyDictionary<string, int> counts)
    {
        var maximum = new[] { identity.PrivateKeyPath, identity.PublicKeyPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => counts.GetValueOrDefault(NormalizePath(path), 1))
            .DefaultIfEmpty(1)
            .Max();
        return maximum > 1 ? $"共享（{maximum} 个身份）" : "独立";
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private IdentityRow? SelectedRow()
        => _grid.CurrentRow?.DataBoundItem as IdentityRow;

    private GitIdentity? SelectedIdentity()
    {
        if (_grid.CurrentRow?.DataBoundItem is not IdentityRow row)
        {
            MessageBox.Show(this, "请先选择一个身份。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return _identities.FirstOrDefault(item => string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class IdentityRow
    {
        public string Id { get; init; } = string.Empty;
        public string PublicKeyPath { get; init; } = string.Empty;
        public string Fingerprint { get; init; } = string.Empty;
        public string 显示名称 { get; init; } = string.Empty;
        public string Git服务 { get; init; } = string.Empty;
        public string 账号 { get; init; } = string.Empty;
        public string HostAlias { get; init; } = string.Empty;
        public string 私钥 { get; init; } = string.Empty;
        public string 公钥格式 { get; init; } = string.Empty;
        public string 公钥文件 { get; init; } = string.Empty;
        public string SHA256指纹 { get; init; } = string.Empty;
        public string OpenSSH格式 { get; init; } = string.Empty;
        public string 配置路径 { get; init; } = string.Empty;
        public string SSH配置 { get; init; } = string.Empty;
        public string 密钥使用 { get; init; } = string.Empty;
    }
}
