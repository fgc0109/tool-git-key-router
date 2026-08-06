using GitKeyRouter.App.Forms;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.App.Presentation;

public static class GitSshBackendUi
{
    public static async Task<bool> EnsureOpenSshAsync(
        IWin32Window owner,
        ApplicationServices services,
        CancellationToken cancellationToken = default)
    {
        var inspectionResult = await services.GitSshBackendService
            .InspectAsync(cancellationToken)
            .ConfigureAwait(true);
        if (!inspectionResult.Success || inspectionResult.Value is null)
        {
            UiHelpers.ShowErrors(owner, inspectionResult);
            return false;
        }

        var inspection = inspectionResult.Value;
        if (inspection.IsOpenSsh)
        {
            return true;
        }

        if (inspection.EnvironmentBlockers.Count > 0)
        {
            using var blockers = new TextViewForm(
                AppLocalization.T("Git SSH 后端不兼容", "Incompatible Git SSH backend"),
                FormatEnvironmentBlockers(inspection));
            blockers.ShowDialog(owner);
            return false;
        }

        if (!inspection.CanApplyOpenSshFix)
        {
            MessageBox.Show(
                owner,
                AppLocalization.T(
                    $"Git 当前 SSH 后端无法确认为 OpenSSH。\r\n\r\n后端：{inspection.DisplayName}\r\n来源：{inspection.Source}\r\n可执行文件：{inspection.EffectiveExecutable ?? "<Git 默认>"}\r\nVariant：{inspection.EffectiveVariant ?? "<自动>"}\r\n\r\n为避免修改未知的自定义 SSH 包装器，GitKeyRouter 不会自动继续。请在 Git for Windows 中选择 OpenSSH 后重试。",
                    $"Git's current SSH backend cannot be confirmed as OpenSSH.\r\n\r\nBackend: {inspection.DisplayName}\r\nSource: {inspection.Source}\r\nExecutable: {inspection.EffectiveExecutable ?? "<Git default>"}\r\nVariant: {inspection.EffectiveVariant ?? "<automatic>"}\r\n\r\nGitKeyRouter will not replace an unknown custom SSH wrapper automatically. Select OpenSSH in Git for Windows and retry."),
                "GitKeyRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        using var confirmation = new DiffPreviewForm(
            AppLocalization.T("将 Git 切换到 OpenSSH", "Switch Git to OpenSSH"),
            FormatFixPreview(inspection),
            AppLocalization.T("使用 OpenSSH 并继续", "Use OpenSSH and continue"));
        if (confirmation.ShowDialog(owner) != DialogResult.OK)
        {
            return false;
        }

        var applied = await services.GitSshBackendService
            .UseOpenSshAsync(inspection, cancellationToken)
            .ConfigureAwait(true);
        if (!applied.Success || applied.Value is null)
        {
            UiHelpers.ShowErrors(owner, applied);
            return false;
        }

        MessageBox.Show(
            owner,
            AppLocalization.T(
                $"Git 已切换到 OpenSSH。\r\n\r\ncore.sshCommand = {applied.Value.CoreSshCommand}\r\nssh.variant = {applied.Value.SshVariant}\r\n\r\n现在继续执行连接测试。",
                $"Git now uses OpenSSH.\r\n\r\ncore.sshCommand = {applied.Value.CoreSshCommand}\r\nssh.variant = {applied.Value.SshVariant}\r\n\r\nThe connection test will now continue."),
            "GitKeyRouter",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return true;
    }

    private static string FormatFixPreview(GitSshBackendInspection inspection)
        => string.Join(Environment.NewLine,
        [
            AppLocalization.T(
                "GitKeyRouter 的 HostAlias、IdentityFile 和 known_hosts 都使用 OpenSSH 格式；PuTTY/Plink 不读取这些文件。",
                "GitKeyRouter's HostAlias, IdentityFile, and known_hosts settings use OpenSSH format; PuTTY/Plink does not read these files."),
            string.Empty,
            $"{AppLocalization.T("当前后端", "Current backend")}: {inspection.DisplayName}",
            $"{AppLocalization.T("来源", "Source")}: {inspection.Source}",
            $"{AppLocalization.T("当前可执行文件", "Current executable")}: {inspection.EffectiveExecutable ?? "<Git default>"}",
            $"ssh.variant: {inspection.EffectiveVariant ?? "<automatic>"}",
            string.Empty,
            AppLocalization.T("确认后写入 Git 全局配置：", "After confirmation, write these global Git settings:"),
            $"+ core.sshCommand = {inspection.SelectedOpenSshPath}",
            "+ ssh.variant = ssh",
            string.Empty,
            AppLocalization.T(
                "写入前会重新检查当前后端；任一步失败都会尝试恢复原有全局值。不会修改 SSH 私钥。",
                "The current backend is rechecked before writing. If any step fails, GitKeyRouter attempts to restore the previous global values. SSH private keys are not modified.")
        ]);

    private static string FormatEnvironmentBlockers(GitSshBackendInspection inspection)
        => string.Join(Environment.NewLine,
        [
            AppLocalization.T(
                "Git 当前被环境变量强制使用 PuTTY/Plink。Git 全局配置无法覆盖这些环境变量，因此 GitKeyRouter 不会执行无效的一键修复。",
                "Git is currently forced to use PuTTY/Plink by environment variables. Global Git settings cannot override them, so GitKeyRouter will not apply an ineffective fix."),
            string.Empty,
            $"{AppLocalization.T("当前后端", "Current backend")}: {inspection.DisplayName}",
            $"{AppLocalization.T("来源", "Source")}: {inspection.Source}",
            string.Empty,
            AppLocalization.T("阻止项：", "Blocking overrides:"),
            .. inspection.EnvironmentBlockers.Select(value => $"- {value}"),
            string.Empty,
            AppLocalization.T(
                "请在 Git for Windows 中选择 OpenSSH，或删除这些环境变量，然后完全退出并重新启动 GitKeyRouter。",
                "Select OpenSSH in Git for Windows, or remove these environment variables, then fully exit and restart GitKeyRouter.")
        ]);
}
