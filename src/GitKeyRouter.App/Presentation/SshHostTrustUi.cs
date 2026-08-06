using GitKeyRouter.App.Forms;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.App.Presentation;

public static class SshHostTrustUi
{
    public static async Task<bool> PromptAndTrustAsync(
        IWin32Window owner,
        ApplicationServices services,
        GitServiceInstance service,
        CancellationToken cancellationToken = default)
    {
        var previewResult = await services.SshHostTrustService
            .BuildPreviewAsync(service, cancellationToken)
            .ConfigureAwait(true);
        if (!previewResult.Success || previewResult.Value is null)
        {
            UiHelpers.ShowErrors(owner, previewResult);
            return false;
        }

        var preview = previewResult.Value;
        if (preview.Status == SshHostTrustStatus.Trusted)
        {
            MessageBox.Show(
                owner,
                AppLocalization.T(
                    "known_hosts 已包含与当前服务器扫描结果一致的密钥。连接仍失败时，请检查自定义 UserKnownHostsFile、文件权限或 SSH 环境覆盖。",
                    "known_hosts already contains a key matching the current server scan. If the connection still fails, inspect a custom UserKnownHostsFile, file permissions, or SSH environment overrides."),
                "GitKeyRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (preview.Status == SshHostTrustStatus.Conflict)
        {
            using var conflict = new TextViewForm(
                AppLocalization.T("SSH 主机密钥冲突", "SSH host-key conflict"),
                FormatConflict(preview));
            conflict.ShowDialog(owner);
            return false;
        }

        using var confirmation = new DiffPreviewForm(
            AppLocalization.T("验证并信任 SSH 主机", "Verify and trust SSH host"),
            FormatTrustPreview(preview),
            AppLocalization.T("信任并重试", "Trust and retry"));
        if (confirmation.ShowDialog(owner) != DialogResult.OK)
        {
            return false;
        }

        var applied = await services.SshHostTrustService
            .TrustAsync(service, preview, cancellationToken)
            .ConfigureAwait(true);
        if (!applied.Success || applied.Value is null)
        {
            UiHelpers.ShowErrors(owner, applied);
            return false;
        }

        var backup = string.IsNullOrWhiteSpace(applied.Value.BackupPath)
            ? AppLocalization.T("原文件不存在，无需备份。", "The original file did not exist; no backup was needed.")
            : AppLocalization.T(
                $"备份：{applied.Value.BackupPath}",
                $"Backup: {applied.Value.BackupPath}");
        MessageBox.Show(
            owner,
            AppLocalization.T(
                $"已信任 {preview.HostIdentifier} 的 {applied.Value.AddedKeyCount} 个主机密钥。\r\n\r\n{backup}\r\n\r\n现在将自动重试连接。",
                $"Trusted {applied.Value.AddedKeyCount} host key(s) for {preview.HostIdentifier}.\r\n\r\n{backup}\r\n\r\nThe connection will now be retried."),
            "GitKeyRouter",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return true;
    }

    private static string FormatTrustPreview(SshHostTrustPreview preview)
    {
        var title = AppLocalization.T(
            "这是首次连接所需的服务器身份确认，不是私钥密码。",
            "This is server identity confirmation for the first connection, not a private-key password.");
        var warning = AppLocalization.T(
            "请通过服务器管理员或其他可信渠道核对以下 SHA-256 指纹。确认后，GitKeyRouter 会在写入前重新扫描；主机密钥或 known_hosts 发生变化时将拒绝操作。",
            "Verify these SHA-256 fingerprints with the server administrator or another trusted channel. After confirmation, GitKeyRouter rescans before writing and refuses the operation if the host keys or known_hosts changed.");
        return string.Join(Environment.NewLine,
        [
            title,
            string.Empty,
            $"{AppLocalization.T("服务", "Service")}: {preview.ServiceDisplayName}",
            $"{AppLocalization.T("端点", "Endpoint")}: {preview.HostIdentifier}",
            $"known_hosts: {preview.KnownHostsPath}",
            string.Empty,
            AppLocalization.T("扫描到的服务器主机密钥：", "Scanned server host keys:"),
            .. preview.ScannedKeys.Select(key => $"- {key.KeyType}  {key.Fingerprint}"),
            string.Empty,
            warning,
            string.Empty,
            AppLocalization.T(
                "不会设置或索取私钥 passphrase，也不会使用 StrictHostKeyChecking=no。",
                "No private-key passphrase is set or requested, and StrictHostKeyChecking=no is never used.")
        ]);
    }

    private static string FormatConflict(SshHostTrustPreview preview)
        => string.Join(Environment.NewLine,
        [
            AppLocalization.T(
                "已有 known_hosts 记录与当前服务器扫描结果不一致。为防止中间人攻击，GitKeyRouter 不会自动删除或覆盖旧记录。",
                "Existing known_hosts entries differ from the current server scan. To prevent man-in-the-middle attacks, GitKeyRouter will not delete or replace them automatically."),
            string.Empty,
            $"{AppLocalization.T("端点", "Endpoint")}: {preview.HostIdentifier}",
            $"known_hosts: {preview.KnownHostsPath}",
            string.Empty,
            AppLocalization.T("当前服务器扫描：", "Current server scan:"),
            .. preview.ScannedKeys.Select(key => $"- {key.KeyType}  {key.Fingerprint}"),
            string.Empty,
            AppLocalization.T("已有记录：", "Existing entries:"),
            .. preview.ExistingKeys.Select(key => $"- {key.KeyType}  {key.Fingerprint}"),
            preview.ExistingEntriesContainMarkers
                ? AppLocalization.T(
                    "- 检测到 @revoked / @cert-authority 标记；不会自动更改该记录。",
                    "- An @revoked / @cert-authority marker was detected; this entry will not be changed automatically.")
                : string.Empty,
            string.Empty,
            AppLocalization.T(
                "请先向服务器管理员确认是否更换过主机密钥，再人工处理冲突记录。",
                "Confirm with the server administrator whether the host key changed before manually resolving the conflicting entry.")
        ]);
}
