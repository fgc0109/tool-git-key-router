using System.Text;
using System.Text.Json;
using GitKeyRouter.App.Forms;
using GitKeyRouter.App.Presentation;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;

namespace GitKeyRouter.App.Controls;

public sealed class BackupControl : UserControl, IAsyncRefreshable
{
    private readonly ApplicationServices _services;
    private readonly Action<string> _status;
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<BackupInventoryItem> _inventory = [];

    public BackupControl(ApplicationServices services, Action<string> status)
    {
        _services = services;
        _status = status;
        var header = UiHelpers.CreatePageHeader(
            AppLocalization.T("备份与恢复", "Backup and Restore"),
            AppLocalization.T("本机快照与跨电脑加密迁移", "Local snapshots and encrypted cross-computer transfer"),
            AppLocalization.T(
                "本机快照用于细粒度恢复。便携备份包使用口令加密，可将全部程序设置、SSH Config、Git rewrite 以及身份引用的私钥和公钥迁移到另一台电脑。\r\n\r\n• 导入前会验证口令、认证标签、结构、密钥 SHA-256 和配置版本。\r\n• 密钥会安全重映射到目标电脑的 .ssh\\GitKeyRouter 目录。\r\n• 导入失败时会自动恢复原配置、SSH、Git rewrite、密钥和 Git Profile 文件。\r\n• 便携备份包含私钥；请使用强口令并妥善保管。",
                "Local snapshots support granular recovery. Password-encrypted portable packages transfer all application settings, SSH Config, Git rewrites, and every referenced private/public key to another computer.\r\n\r\n• Import verifies the password, authentication tag, structure, key SHA-256 values, and configuration version before mutation.\r\n• Keys are safely remapped under the target computer's .ssh\\GitKeyRouter directory.\r\n• A failed import automatically restores configuration, SSH, Git rewrites, keys, and Git Profile files.\r\n• Portable packages contain private keys; use a strong password and protect the file."));
        var toolbar = UiHelpers.CreateToolbar();
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("立即创建快照", "Create snapshot now"), async (_, _) => await CreateSnapshotAsync()));
        var exportPortable = UiHelpers.Button(
            AppLocalization.T("导出完整加密备份", "Export complete encrypted backup"),
            async (_, _) => await ExportPortableAsync());
        exportPortable.Name = "ExportPortableBackupButton";
        toolbar.Controls.Add(exportPortable);
        var importPortable = UiHelpers.Button(
            AppLocalization.T("导入完整加密备份", "Import complete encrypted backup"),
            async (_, _) => await ImportPortableAsync());
        importPortable.Name = "ImportPortableBackupButton";
        toolbar.Controls.Add(importPortable);
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("查看内容", "View contents"), async (_, _) => await ViewAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("恢复 SSH Config", "Restore SSH Config"), async (_, _) => await RestoreSshAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("恢复 Git rewrite", "Restore Git rewrites"), async (_, _) => await RestoreGitAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("恢复程序配置", "Restore application config"), async (_, _) => await RestoreAppAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("清理选中异常项", "Clean selected invalid item"), async (_, _) => await CleanSelectedAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("打开备份目录", "Open backup folder"), (_, _) => OpenBackupDirectory()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("刷新", "Refresh"), async (_, _) => await RefreshAsync()));
        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(header);
    }

    public async Task RefreshAsync()
    {
        _inventory = await _services.BackupService.InventoryAsync();
        _grid.DataSource = _inventory.Select((item, index) => new BackupRow
        {
            Index = index,
            时间 = (item.Manifest?.CreatedAt ?? item.LastWriteTimeUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            状态 = FormatStatus(item.Status),
            原因 = item.Manifest?.Reason ?? item.Reason,
            健康说明 = item.Reason,
            文件与完整性 = item.Details.Count == 0 ? "<无>" : string.Join("; ", item.Details),
            可清理 = item.CanClean ? "是" : "否"
        }).ToList();
        if (_grid.Columns[nameof(BackupRow.Index)] is { } indexColumn)
        {
            indexColumn.Visible = false;
        }

        _status($"已扫描 {_inventory.Count} 个备份目录，其中 {_inventory.Count(item => item.Status == BackupHealthStatus.Complete)} 个完整");
    }

    private async Task CreateSnapshotAsync()
    {
        var manifest = await _services.BackupService.CreateSnapshotAsync("Manual backup");
        _status($"已创建备份：{manifest.BackupDirectory}");
        await RefreshAsync();
    }

    private async Task ExportPortableAsync()
    {
        using var destination = new SaveFileDialog
        {
            Title = AppLocalization.T("导出完整加密备份", "Export complete encrypted backup"),
            Filter = "GitKeyRouter portable backup (*.gkrbackup)|*.gkrbackup|All files (*.*)|*.*",
            DefaultExt = "gkrbackup",
            AddExtension = true,
            FileName = $"GitKeyRouter-{DateTime.Now:yyyyMMdd-HHmmss}.gkrbackup"
        };
        if (destination.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        using var password = new PortableBackupPasswordForm(confirmPassword: true);
        if (password.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.PortableBackupService.ExportAsync(destination.FileName, password.Password);
        if (!result.Success || result.Value is null)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        var message = AppLocalization.T(
            $"完整加密备份已导出。\r\n\r\n{result.Value.Summary}\r\n\r\n文件：{destination.FileName}",
            $"Complete encrypted backup exported.\r\n\r\n{result.Value.Summary}\r\n\r\nFile: {destination.FileName}");
        MessageBox.Show(this, message, "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _status(result.Message);
    }

    private async Task ImportPortableAsync()
    {
        using var source = new OpenFileDialog
        {
            Title = AppLocalization.T("导入完整加密备份", "Import complete encrypted backup"),
            Filter = "GitKeyRouter portable backup (*.gkrbackup)|*.gkrbackup|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (source.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        using var password = new PortableBackupPasswordForm(confirmPassword: false);
        if (password.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var preview = await _services.PortableBackupService.InspectAsync(source.FileName, password.Password);
        if (!preview.Success || preview.Value is null)
        {
            UiHelpers.ShowErrors(this, preview);
            return;
        }

        var confirmation = AppLocalization.T(
            $"已验证加密备份：\r\n{preview.Value.Summary}\r\n\r\n继续后将替换当前程序设置、SSH Config、Git rewrite 和 Git Profile 文件，并把备份中的密钥写入本机托管目录。发生错误时会自动回滚。\r\n\r\n是否继续？",
            $"Encrypted backup verified:\r\n{preview.Value.Summary}\r\n\r\nContinuing replaces current application settings, SSH Config, Git rewrites, and Git Profile files, and writes the packaged keys into the managed local directory. Errors trigger automatic rollback.\r\n\r\nContinue?");
        if (MessageBox.Show(
                this,
                confirmation,
                AppLocalization.T("确认完整导入", "Confirm complete import"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var result = await _services.PortableBackupService.ImportAsync(source.FileName, password.Password);
        if (!result.Success || result.Value is null)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        var message = AppLocalization.T(
            $"完整备份已导入。\r\n\r\n身份：{result.Value.IdentityCount}\r\n密钥文件：{result.Value.KeyFileCount}\r\nGit rewrite：{result.Value.GitRewriteCount}\r\n密钥目录：{result.Value.ManagedKeyDirectory}\r\n\r\n请刷新各页面；为确保所有视图重新加载，建议重启 GitKeyRouter。",
            $"Complete backup imported.\r\n\r\nIdentities: {result.Value.IdentityCount}\r\nKey files: {result.Value.KeyFileCount}\r\nGit rewrites: {result.Value.GitRewriteCount}\r\nKey directory: {result.Value.ManagedKeyDirectory}\r\n\r\nRefresh the pages; restarting GitKeyRouter is recommended so every view reloads the imported state.");
        MessageBox.Show(this, message, "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _status(result.Message);
    }

    private async Task ViewAsync()
    {
        var item = SelectedInventoryItem();
        if (item is null)
        {
            return;
        }

        if (item.Status != BackupHealthStatus.Complete || item.Manifest is null)
        {
            var healthText = $"Directory: {item.BackupDirectory}{Environment.NewLine}"
                + $"Status: {item.Status}{Environment.NewLine}"
                + $"Reason: {item.Reason}{Environment.NewLine}"
                + $"Can clean: {item.CanClean}{Environment.NewLine}{Environment.NewLine}"
                + string.Join(Environment.NewLine, item.Details);
            using var healthForm = new TextViewForm("备份健康详情", healthText);
            healthForm.ShowDialog(this);
            return;
        }

        var snapshot = await _services.BackupService.ReadAsync(item.BackupDirectory);
        var builder = new StringBuilder();
        builder.AppendLine("Manifest");
        builder.AppendLine(JsonSerializer.Serialize(snapshot.Manifest, new JsonSerializerOptions { WriteIndented = true }));
        builder.AppendLine();
        builder.AppendLine("Application config");
        builder.AppendLine(snapshot.AppConfigText ?? "<not present>");
        builder.AppendLine();
        builder.AppendLine("SSH config");
        builder.AppendLine(snapshot.SshConfigText ?? "<not present>");
        builder.AppendLine();
        builder.AppendLine("Git URL rewrites");
        foreach (var rule in snapshot.GitUrlRewrites)
        {
            builder.AppendLine($"{rule.ConfigKey} = {rule.InsteadOfUrl}");
        }

        using var form = new TextViewForm("备份内容", builder.ToString());
        form.ShowDialog(this);
    }

    private async Task RestoreSshAsync()
    {
        var backup = SelectedBackup();
        if (backup is null)
        {
            return;
        }

        var snapshot = await _services.BackupService.ReadAsync(backup.BackupDirectory);
        var current = await _services.SshConfigService.ReadRawAsync();
        var target = snapshot.Manifest.SshConfigExisted ? snapshot.SshConfigText ?? string.Empty : string.Empty;
        var diffText = TextDiffService.CreateSimpleDiff(current, target, "ssh_config.current", "ssh_config.backup");
        using var diff = new DiffPreviewForm("恢复 SSH Config", diffText, "恢复此备份");
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.BackupService.RestoreSshConfigAsync(backup.BackupDirectory);
        ShowRestoreResult(result);
    }

    private async Task RestoreAppAsync()
    {
        var backup = SelectedBackup();
        if (backup is null)
        {
            return;
        }

        var snapshot = await _services.BackupService.ReadAsync(backup.BackupDirectory);
        var current = _services.FileSystem.FileExists(_services.Paths.ConfigPath)
            ? await _services.FileSystem.ReadAllTextAsync(_services.Paths.ConfigPath)
            : string.Empty;
        var target = snapshot.Manifest.AppConfigExisted ? snapshot.AppConfigText ?? string.Empty : string.Empty;
        var diffText = TextDiffService.CreateSimpleDiff(current, target, "app_config.current", "app_config.backup");
        using var diff = new DiffPreviewForm("恢复程序配置", diffText, "恢复此备份");
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.BackupService.RestoreAppConfigAsync(backup.BackupDirectory);
        ShowRestoreResult(result);
    }

    private async Task RestoreGitAsync()
    {
        var backup = SelectedBackup();
        if (backup is null)
        {
            return;
        }

        var snapshot = await _services.BackupService.ReadAsync(backup.BackupDirectory);
        if (!string.IsNullOrWhiteSpace(snapshot.Manifest.GitRewriteCaptureError))
        {
            MessageBox.Show(this, snapshot.Manifest.GitRewriteCaptureError, "该备份没有可靠 Git 快照", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var current = await _services.GitUrlRewriteService.GetActualRulesAsync();
        var builder = new StringBuilder();
        builder.AppendLine("Current Git URL rewrites:");
        foreach (var rule in current)
        {
            builder.Append("- ").Append(rule.ConfigKey).Append(" = ").AppendLine(rule.InsteadOfUrl);
        }

        builder.AppendLine();
        builder.AppendLine("Backup Git URL rewrites:");
        foreach (var rule in snapshot.GitUrlRewrites)
        {
            builder.Append("+ ").Append(rule.ConfigKey).Append(" = ").AppendLine(rule.InsteadOfUrl);
        }

        using var diff = new DiffPreviewForm("恢复 Git URL rewrite", builder.ToString(), "恢复此备份");
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.BackupService.RestoreGitRewritesAsync(backup.BackupDirectory);
        ShowRestoreResult(result);
    }

    private void ShowRestoreResult(OperationResult result)
    {
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        MessageBox.Show(this, result.Message, "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _status(result.Message);
    }

    private async Task CleanSelectedAsync()
    {
        var item = SelectedInventoryItem();
        if (item is null)
        {
            return;
        }

        var plan = await _services.BackupService.PreviewCleanupAsync([item.BackupDirectory]);
        if (!plan.HasTargets)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, plan.Rejected.DefaultIfEmpty("当前项目不可安全清理。")),
                "未生成清理计划",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var target = plan.Targets[0];
        var preview = $"状态：{target.Status}{Environment.NewLine}"
            + $"原因：{target.Reason}{Environment.NewLine}"
            + $"目录：{target.BackupDirectory}{Environment.NewLine}{Environment.NewLine}"
            + "确认后将永久删除这个异常备份目录。";
        if (MessageBox.Show(this, preview, "确认清理异常备份", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var result = await _services.BackupService.CleanAsync(plan);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
        }
        else
        {
            _status(result.Message);
        }

        await RefreshAsync();
    }

    private BackupManifest? SelectedBackup()
    {
        var item = SelectedInventoryItem();
        if (item is null)
        {
            return null;
        }

        if (item.Status != BackupHealthStatus.Complete || item.Manifest is null)
        {
            MessageBox.Show(this, "只能查看或恢复通过完整性检查的完整备份。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return item.Manifest;
    }

    private BackupInventoryItem? SelectedInventoryItem()
    {
        if (_grid.CurrentRow?.DataBoundItem is not BackupRow row || row.Index < 0 || row.Index >= _inventory.Count)
        {
            MessageBox.Show(this, "请先选择一个备份目录。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return _inventory[row.Index];
    }

    private static string FormatStatus(BackupHealthStatus status)
        => status switch
        {
            BackupHealthStatus.Complete => AppLocalization.T("完整", "Complete"),
            BackupHealthStatus.Pending => AppLocalization.T("未完成", "Pending"),
            BackupHealthStatus.Damaged => AppLocalization.T("损坏", "Damaged"),
            BackupHealthStatus.Unsupported => AppLocalization.T("版本不支持", "Unsupported"),
            _ => AppLocalization.T("未知", "Unknown")
        };

    private void OpenBackupDirectory()
    {
        Directory.CreateDirectory(_services.Paths.BackupRootDirectory);
        var startInfo = new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        startInfo.ArgumentList.Add(_services.Paths.BackupRootDirectory);
        System.Diagnostics.Process.Start(startInfo);
    }

    private sealed class BackupRow
    {
        public int Index { get; init; }
        public string 时间 { get; init; } = string.Empty;
        public string 状态 { get; init; } = string.Empty;
        public string 原因 { get; init; } = string.Empty;
        public string 健康说明 { get; init; } = string.Empty;
        public string 文件与完整性 { get; init; } = string.Empty;
        public string 可清理 { get; init; } = string.Empty;
    }
}
